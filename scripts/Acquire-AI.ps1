param([switch]$RebuildCpu)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = (Resolve-Path "$PSScriptRoot/..").Path
$tools = Join-Path $repo '.tools\ai'
$assets = Join-Path $repo 'vendor\ai'
New-Item -ItemType Directory -Force $tools,$assets | Out-Null
function Get-PinnedArchive([string]$Url,[string]$Path,[string]$Expected) {
    if (-not (Test-Path -LiteralPath $Path)) { Invoke-WebRequest $Url -OutFile $Path }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Expected) { throw "Archive integrity check failed: $Path" }
}
Get-PinnedArchive 'https://github.com/nihui/waifu2x-ncnn-vulkan/releases/download/20250915/waifu2x-ncnn-vulkan-20250915-windows.zip' (Join-Path $tools 'waifu2x.zip') '7425be94b94e4c8f37a1e433ac0e0100c43790e2c37418f4b65d8235adfbdc87'
$expanded = Join-Path $tools 'extracted'
if (-not (Test-Path -LiteralPath $expanded)) { Expand-Archive -LiteralPath (Join-Path $tools 'waifu2x.zip') -DestinationPath $expanded }
$bundle = Join-Path $expanded 'waifu2x-ncnn-vulkan-20250915-windows'
Copy-Item -LiteralPath (Join-Path $bundle 'waifu2x-ncnn-vulkan.exe') -Destination $assets
Copy-Item -LiteralPath (Join-Path $bundle 'vcomp140.dll') -Destination $assets
New-Item -ItemType Directory -Force (Join-Path $assets 'models-cunet') | Out-Null
foreach ($file in @('scale2.0x_model.bin','scale2.0x_model.param','noise1_scale2.0x_model.bin','noise1_scale2.0x_model.param')) { Copy-Item -LiteralPath (Join-Path $bundle "models-cunet\$file") -Destination (Join-Path $assets 'models-cunet') }
# CPU worker and signed application-local runtimes are versioned in vendor/ai; normal release builds need no compiler.
if ($RebuildCpu) {
    Get-PinnedArchive 'https://github.com/Tencent/ncnn/releases/download/20261020/ncnn-20261020-windows-vs2022.zip' (Join-Path $tools 'ncnn.zip') 'ef9fe4463e5db0f41484ad25e91686e709b91e400858639aed631c280dfa91d4'
    if (-not (Test-Path -LiteralPath (Join-Path $tools 'ncnn'))) { Expand-Archive -LiteralPath (Join-Path $tools 'ncnn.zip') -DestinationPath (Join-Path $tools 'ncnn') }
    $sdkVersion = '10.0.26100.3916'
    $sdkHashes = @{'microsoft.windows.sdk.cpp'='21dfb5db3397425ee428e0fd54cedc51af52ddd10c82433a7cdb483ecbb284ad';'microsoft.windows.sdk.cpp.x64'='ce31d6d47c1333c79501814538ad5f9dbdfb78ac0123fb0ec7978fccbc6a7e82'}
    foreach($id in $sdkHashes.Keys) {
        $zip = Join-Path $tools "$id.zip"
        Get-PinnedArchive "https://api.nuget.org/v3-flatcontainer/$id/$sdkVersion/$id.$sdkVersion.nupkg" $zip $sdkHashes[$id]
        if (-not (Test-Path -LiteralPath (Join-Path $tools $id))) { Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $tools $id) }
    }
    & "$PSScriptRoot/Build-CpuWorker.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'CPU worker rebuild failed' }
    Write-Warning 'A rebuilt worker has a new PE timestamp/hash. Review and update the compiled EnhancementService pin after testing.'
}
$manifest = Get-ChildItem -LiteralPath $assets -Recurse -File | Where-Object { $_.Name -ne 'assets.json' } | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($assets.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$manifestJson = ($manifest | ConvertTo-Json -Depth 5).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText((Join-Path $assets 'assets.json'), $manifestJson, [Text.UTF8Encoding]::new($false))
Write-Output "Pinned offline AI assets ready: $assets"
