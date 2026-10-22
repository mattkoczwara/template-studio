[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$DotnetPath,
    [string]$IsccPath,
    [switch]$SkipTests,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repoRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.tools/packages'
if (-not $DotnetPath) {
    $localSdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
if (-not $IsccPath) {
    $candidates = @(
        (Join-Path $repoRoot '.tools/inno/compiler/ISCC.exe'),
        (Join-Path ${env:ProgramFiles} 'Inno Setup 7/ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7/ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $IsccPath) { throw 'Inno Setup 7.1 compiler not found. See docs/DISTRIBUTION.md.' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have major.minor.patch format.' }
$finalPublishPath = Join-Path $repoRoot 'artifacts/publish/win-x64'
$publishPath = Join-Path $repoRoot ('artifacts/publish/.stage-' + [Guid]::NewGuid().ToString('N'))
$installerPath = Join-Path $repoRoot 'artifacts/installer'
$reportsPath = Join-Path $repoRoot 'artifacts/verification'
New-Item -ItemType Directory -Force -Path $publishPath,$installerPath,$reportsPath | Out-Null

if (-not $SkipTests) {
    foreach ($checkName in @('Mosaic.Core.Checks','Mosaic.Storage.Checks','Mosaic.Output.Smoke','Mosaic.Imaging.Checks','Mosaic.App.Checks')) {
        $checkProject = Join-Path $repoRoot "tests/$checkName/$checkName.csproj"
        if (-not (Test-Path -LiteralPath $checkProject)) { throw "Required release check project missing: $checkName" }
        & $DotnetPath run --project $checkProject -c Release -r win-x64 -p:RestoreLockedMode=true
        if ($LASTEXITCODE -ne 0) { throw "$checkName failed." }
    }
}

$publishArgs = @('publish','src/Mosaic.App/Mosaic.App.csproj','-c','Release','-r','win-x64','--self-contained','true',
    '-p:PublishTrimmed=false','-p:PublishAot=false','-p:PublishSingleFile=false','-p:RestoreLockedMode=true',"-p:Version=$Version",'-o',$publishPath)
if ($NoRestore) { $publishArgs += '--no-restore' }
& $DotnetPath @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publication failed.' }

$requiredFiles = @('MosaicTemplateStudio.exe','MosaicTemplateStudio.dll','MosaicTemplateStudio.runtimeconfig.json',
    'hostfxr.dll','hostpolicy.dll','coreclr.dll','PresentationFramework.dll','ai/waifu2x-ncnn-vulkan.exe',
    'ai/mosaic-cunet-cpu.exe','ai/msvcp140.dll','ai/vcomp140.dll','ai/vcruntime140.dll','ai/vcruntime140_1.dll',
    'ai/assets.json','ai/models-cunet/scale2.0x_model.bin','ai/models-cunet/scale2.0x_model.param',
    'ai/models-cunet/noise1_scale2.0x_model.bin','ai/models-cunet/noise1_scale2.0x_model.param',
    'ai/LICENSE-Microsoft-runtime.txt','ai/Microsoft-REDIST.txt','ai/LICENSE-ncnn.txt',
    'ai/LICENSE-waifu2x-model.txt','ai/LICENSE-waifu2x-ncnn.txt',
    'licenses/PDFsharp-6.2.4-LICENSE.txt','licenses/Magick.NET-15.0.0-LICENSE.txt',
    'licenses/Magick.NET-15.0.0-ThirdPartyNotices.txt','licenses/dotnet-runtime-10.0.13-LICENSE.txt',
    'licenses/dotnet-runtime-10.0.13-ThirdPartyNotices.txt','licenses/dotnet-desktop-10.0.13-LICENSE.txt')
foreach ($relative in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $relative))) { throw "Offline package is missing $relative" }
}
$modelFiles = Get-ChildItem -LiteralPath (Join-Path $publishPath 'ai') -Filter '*.bin' -Recurse
if (-not $modelFiles) { throw 'Offline package does not contain AI model weights.' }
$aiRoot = [IO.Path]::GetFullPath((Join-Path $publishPath 'ai'))
$aiAssets = Get-Content -LiteralPath (Join-Path $aiRoot 'assets.json') -Raw | ConvertFrom-Json
foreach ($asset in $aiAssets) {
    $assetPath = [IO.Path]::GetFullPath((Join-Path $aiRoot $asset.path))
    if (-not $assetPath.StartsWith($aiRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'AI manifest contains an invalid path.' }
    if (-not (Test-Path -LiteralPath $assetPath)) { throw "AI payload is missing $($asset.path)" }
    if ((Get-Item -LiteralPath $assetPath).Length -ne $asset.bytes -or
        (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
        throw "AI payload integrity check failed: $($asset.path)"
    }
}
$notices = Get-ChildItem -LiteralPath $publishPath -File | Where-Object { $_.Name -match 'NOTICE|LICENSE' }
if (-not $notices) { throw 'Third-party license notices are missing.' }
$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishPath 'MosaicTemplateStudio.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.framework -or $runtimeConfig.runtimeOptions.frameworks -or
    -not $runtimeConfig.runtimeOptions.includedFrameworks) { throw 'Runtime configuration is not self-contained.' }
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    if ($framework.version -ne '10.0.13') { throw "Unexpected runtime version $($framework.version); update licenses and pin it intentionally." }
}

# Fresh publication avoids accidentally redistributing stale files from an earlier build.
# Preserve an existing output folder as a recoverable build artifact instead of deleting it.
$workspacePrefix = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
foreach ($candidate in @($publishPath,$finalPublishPath)) {
    if (-not ([IO.Path]::GetFullPath($candidate)).StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publication move target escaped the workspace.'
    }
}
if (Test-Path -LiteralPath $finalPublishPath) {
    $previous = Join-Path $repoRoot ('artifacts/publish/.previous-' + [Guid]::NewGuid().ToString('N'))
    if (-not ([IO.Path]::GetFullPath($previous)).StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Previous publication target escaped the workspace.'
    }
    Move-Item -LiteralPath $finalPublishPath -Destination $previous
}
Move-Item -LiteralPath $publishPath -Destination $finalPublishPath
$publishPath = $finalPublishPath

$hashes = Get-ChildItem -LiteralPath $publishPath -File -Recurse | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($publishPath, $_.FullName); bytes = $_.Length;
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$hashes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportsPath 'published-files.json') -Encoding utf8

& $IsccPath "--define=AppVersion=$Version" "--define=PublishDir=$publishPath" "--define=InstallerOutput=$installerPath" 'installer/MosaicTemplateStudio.iss'
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installerFile = Join-Path $installerPath "MosaicTemplateStudio-$Version-win-x64-Setup.exe"
$installerHash = (Get-FileHash -LiteralPath $installerFile -Algorithm SHA256).Hash.ToLowerInvariant()
"$installerHash  $([IO.Path]::GetFileName($installerFile))" | Set-Content -LiteralPath "$installerFile.sha256" -Encoding ascii
[ordered]@{ version=$Version; timestamp=[DateTimeOffset]::UtcNow.ToString('O'); installer=$installerFile;
    installerBytes=(Get-Item -LiteralPath $installerFile).Length; sha256=$installerHash;
    publishedFiles=@($hashes).Count; publishedBytes=(Get-ChildItem -LiteralPath $publishPath -File -Recurse | Measure-Object -Property Length -Sum).Sum;
    signed=((Get-AuthenticodeSignature -LiteralPath $installerFile).Status -eq 'Valid') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportsPath 'release-package.json') -Encoding utf8
Write-Host "Installer: $installerFile"
Write-Host "SHA-256: $installerHash"
