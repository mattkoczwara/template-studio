[CmdletBinding()]
param(
    [string]$InstallerPath,
    [string]$ManifestPath,
    [ValidateSet('english','polish')][string]$Language = 'english'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $InstallerPath) { $InstallerPath = Join-Path $repoRoot 'artifacts/installer/MosaicTemplateStudio-1.0.0-win-x64-Setup.exe' }
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
if (-not $ManifestPath) { $ManifestPath = Join-Path $repoRoot 'artifacts/verification/published-files.json' }
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7B8E9EF4-E4D4-46F5-907C-A4DC3970D527}_is1'
if (Test-Path -LiteralPath $uninstallKey) { throw 'An existing Mosaic Template Studio installation is registered. Preserve it; use a clean test account for installer verification.' }
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Mosaic Template Studio.lnk'
$startShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Mosaic Template Studio.lnk'
$projectProgId = 'HKCU:\Software\Classes\MosaicTemplateStudio.Project'
$associationKey = 'HKCU:\Software\Classes\MosaicTemplateStudio.Project\shell\open\command'
foreach ($existing in @($desktopShortcut,$startShortcut,$projectProgId)) {
    if (Test-Path -LiteralPath $existing) { throw "An existing shortcut or project registration must be preserved before installer verification: $existing" }
}
$openWithKey = 'HKCU:\Software\Classes\.mosaic\OpenWithProgids'
if ((Test-Path -LiteralPath $openWithKey) -and (Get-Item -LiteralPath $openWithKey).GetValueNames().Contains('MosaicTemplateStudio.Project')) {
    throw 'An existing Mosaic Open With registration must be preserved before installer verification.'
}
$testRoot = Join-Path $repoRoot ('artifacts/installer-checks/' + [Guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $testRoot 'app'
$resolvedWorkspace = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
$resolvedTarget = [IO.Path]::GetFullPath($appDirectory)
if (-not $resolvedTarget.StartsWith($resolvedWorkspace, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer test target is outside this workspace.' }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$installLog = Join-Path $testRoot 'install.log'
$uninstallLog = Join-Path $testRoot 'uninstall.log'
$sentinel = Join-Path $appDirectory 'personal-project-preserved.mosaic'
$verificationError = $null
$installerProcess = $null
try {
    $setupArgs = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LANG=' + $Language + ' /TASKS="desktopicon,association" /DIR="' + $appDirectory + '" /LOG="' + $installLog + '"'
    $installerProcess = Start-Process -FilePath $InstallerPath -ArgumentList $setupArgs -WindowStyle Hidden -Wait -PassThru
    if ($installerProcess.ExitCode -ne 0) { throw "Installer exited $($installerProcess.ExitCode). See $installLog" }
    Set-Content -LiteralPath $sentinel -Value 'Installer verification sentinel: preserve unknown personal project files.' -Encoding utf8
    if (-not (Test-Path -LiteralPath $uninstallKey)) { throw 'Installer did not register its uninstaller.' }
    $installedBytes = 0L
    foreach ($entry in $manifest) {
        $installedFile = [IO.Path]::GetFullPath((Join-Path $appDirectory $entry.path))
        if (-not $installedFile.StartsWith($resolvedTarget + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Published manifest contains an invalid path.' }
        if (-not (Test-Path -LiteralPath $installedFile)) { throw "Installed file missing: $($entry.path)" }
        $installedHash = (Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($installedHash -ne $entry.sha256) { throw "Installed file checksum mismatch: $($entry.path)" }
        $installedBytes += (Get-Item -LiteralPath $installedFile).Length
    }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($shortcut in @($desktopShortcut,$startShortcut)) {
        if (-not (Test-Path -LiteralPath $shortcut)) { throw "Installer shortcut missing: $shortcut" }
        if ($shell.CreateShortcut($shortcut).TargetPath -ne (Join-Path $appDirectory 'MosaicTemplateStudio.exe')) { throw 'Installer shortcut target differs from installed executable.' }
    }
    $association = (Get-Item -LiteralPath $associationKey).GetValue('')
    if ($association -notlike ('*' + $appDirectory + '*%1*')) { throw 'Project association is missing or points outside the installed app.' }
    $runtimeDirectory = Join-Path $testRoot 'runtime-check'
    $runtimeArguments = '--verify-install "' + $runtimeDirectory + '"'
    $runtimeProcess = Start-Process -FilePath (Join-Path $appDirectory 'MosaicTemplateStudio.exe') -ArgumentList $runtimeArguments -WorkingDirectory $appDirectory -WindowStyle Hidden -PassThru
    if (-not $runtimeProcess.WaitForExit(120000)) {
        $runtimeProcess.Kill($true)
        $runtimeProcess.WaitForExit()
        throw 'Installed application runtime verification timed out after 120 seconds.'
    }
    if ($runtimeProcess.ExitCode -ne 0) { throw "Installed application runtime verification exited $($runtimeProcess.ExitCode). See $runtimeDirectory" }
    $runtimeReportPath = Join-Path $runtimeDirectory 'installed-runtime.json'
    if (-not (Test-Path -LiteralPath $runtimeReportPath)) { throw 'Installed application runtime verification did not produce its report.' }
    $runtimeReport = Get-Content -LiteralPath $runtimeReportPath -Raw | ConvertFrom-Json
    if ($runtimeReport.passed -ne $true) { throw "Installed application runtime journey failed. See $runtimeReportPath" }
}
catch {
    $verificationError = $_
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $testRoot 'failure.txt') -Encoding utf8
    throw
}
finally {
    # A failed payload/runtime check must not strand our disposable installation or
    # shortcuts. The normal uninstaller preserves personal files even on failure.
    $uninstaller = Join-Path $appDirectory 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        try {
            $uninstallArgs = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + $uninstallLog + '"'
            $uninstallerProcess = Start-Process -FilePath $uninstaller -ArgumentList $uninstallArgs -WindowStyle Hidden -Wait -PassThru
            if ($uninstallerProcess.ExitCode -ne 0) { throw "Uninstaller exited $($uninstallerProcess.ExitCode). See $uninstallLog" }
            if ($installerProcess -and $installerProcess.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $sentinel)) { throw 'Uninstall removed a personal project file.' }
            if (Test-Path -LiteralPath (Join-Path $appDirectory 'MosaicTemplateStudio.exe')) { throw 'Uninstall left the registered application executable behind.' }
            if (Test-Path -LiteralPath $uninstallKey) { throw 'Uninstall left its registration behind.' }
            foreach ($shortcut in @($desktopShortcut,$startShortcut)) {
                if (Test-Path -LiteralPath $shortcut) { throw 'Uninstall left an application shortcut behind.' }
            }
            if (Test-Path -LiteralPath $associationKey) { throw 'Uninstall left its project association behind.' }
        }
        catch {
            if ($verificationError) { Write-Warning "Installer verification also failed to clean up: $_. See $testRoot" }
            else { throw }
        }
    }
}
$report = [ordered]@{
    timestamp = [DateTimeOffset]::UtcNow.ToString('O'); language = $Language;
    installer = $InstallerPath; installerSha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant();
    testDirectory = $testRoot; installExit = $installerProcess.ExitCode; uninstallExit = $uninstallerProcess.ExitCode;
    matchingPayloadFiles = @($manifest).Count; matchingPayloadBytes = $installedBytes;
    shortcutsVerified = $true; associationVerified = $true; personalProjectPreserved = $true;
    cleanOfflineMachineVerified = $false; installedAppRuntimeJourneyVerified = $true;
    installedRuntimeExit = $runtimeProcess.ExitCode; installedRuntimeReport = $runtimeReportPath
}
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'result.json') -Encoding utf8
Write-Host "Installer verification passed. Evidence: $testRoot"
