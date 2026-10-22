#requires -Version 5.1
[CmdletBinding()]
param([string]$DotnetPath)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $DotnetPath) {
    $localSdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$DotnetPath = (Resolve-Path -LiteralPath $DotnetPath).Path
$launcherSource = Join-Path $PSScriptRoot 'Launch-Development.ps1'
$reportRoot = Join-Path $repoRoot ('artifacts/development-launcher-checks/' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $reportRoot 'Mosaic launch fixture'
$fixtureScripts = Join-Path $fixture 'scripts'
$projectDirectory = Join-Path $fixture 'src/Mosaic.App'
$project = Join-Path $projectDirectory 'Mosaic.App.csproj'
$buildRoot = Join-Path $fixture 'artifacts/development/build'
$marker = Join-Path $fixture 'launch.json'
$powershell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$checks = 0
$ownedProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$oldDotnetRoot = $env:DOTNET_ROOT
$oldDotnetRootX64 = $env:DOTNET_ROOT_X64
$fixtureData = Join-Path $fixture 'user-data/MosaicTemplateStudio'
$shortcutDesktop = Join-Path $fixture 'Desktop with spaces'
$lastStartedId = 0

function Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw $Description }
    $script:checks++
    Write-Host "PASS $Description"
}
function Invoke-Launcher([string]$Name, [string]$Sdk = $DotnetPath) {
    $arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "{0}" -DotnetPath "{1}" -NoDialogs' -f (Join-Path $fixtureScripts 'Launch-Development.ps1'), $Sdk
    $process = Start-Process -FilePath $powershell -ArgumentList $arguments -WorkingDirectory $fixture -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $reportRoot ($Name + '.stdout.log')) -RedirectStandardError (Join-Path $reportRoot ($Name + '.stderr.log'))
    # Start-Process -Wait also waits for descendant GUI apps on Windows. Wait only
    # for the launcher process, which must return while its editor remains open.
    $null = $process.Handle
    if (-not $process.WaitForExit(120000)) { throw "Launcher timed out in $Name. See $reportRoot" }
    if ($process.ExitCode -eq 0) {
        $launcherLog = Get-Content -LiteralPath (Join-Path $fixture 'artifacts/development/launcher.log') -Raw
        $pidMatch = [regex]::Match($launcherLog, 'Editor opened\. PID: ([0-9]+)')
        if (-not $pidMatch.Success) { throw 'Successful launcher did not record its new editor process.' }
        $script:lastStartedId = [int]$pidMatch.Groups[1].Value
        $startedProcess = Get-Process -Id $script:lastStartedId -ErrorAction Stop
        if (-not $startedProcess.Path.StartsWith($fixture + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing ownership of an editor outside the disposable fixture.' }
        $script:ownedProcesses.Add($startedProcess)
    }
    return $process.ExitCode
}
function Write-Fixture([string]$Version) {
    $markerLiteral = $marker.Replace('"', '""')
    @"
using System;
using System.IO;
using System.Windows;
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var application = new Application();
        var window = new Window { Title = "Mosaic Template Studio", Width = 360, Height = 180 };
        window.Loaded += (_, _) => File.WriteAllText(@"$markerLiteral", System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = "$Version", WorkingDirectory = Environment.CurrentDirectory, Pid = Environment.ProcessId
        }));
        application.Run(window);
    }
}
"@ | Set-Content -LiteralPath (Join-Path $projectDirectory 'Program.cs') -Encoding UTF8
}
function Read-StartedFixture([string]$Version) {
    # A native window handle exists before WPF Loaded finishes. Wait for this
    # exact process marker rather than reading the previous launch's marker.
    $started = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if (Test-Path -LiteralPath $marker) {
            try { $candidate = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json } catch { $candidate = $null }
            if ($null -ne $candidate -and $candidate.Version -eq $Version -and $candidate.Pid -eq $script:lastStartedId) { $started = $candidate; break }
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    Check ($null -ne $started) 'a real fixture process reached its WPF Loaded event'

    Check ($started.Version -eq $Version) "the launched executable contains source revision $Version"
    Check ($started.WorkingDirectory -eq $fixture) 'the application working directory is the checkout root, including spaces'
    $process = Get-Process -Id $started.Pid -ErrorAction Stop
    Check ($process.Path.StartsWith($fixture + '\', [StringComparison]::OrdinalIgnoreCase)) 'the started process belongs only to the disposable fixture'

    return $process
}
function Stop-Fixture([System.Diagnostics.Process]$Process) {
    $Process.Refresh()
    if (-not $Process.HasExited) {
        if (-not $Process.Path.StartsWith($fixture + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to stop a process outside the disposable fixture.' }
        if (-not $Process.CloseMainWindow()) { throw 'Disposable fixture did not accept a normal window-close request.' }
        if (-not $Process.WaitForExit(10000)) { throw 'Disposable fixture did not exit.' }
    }
}

try {
    New-Item -ItemType Directory -Force -Path $fixtureScripts, $projectDirectory, $fixtureData, $shortcutDesktop | Out-Null
    Copy-Item -LiteralPath $launcherSource -Destination (Join-Path $fixtureScripts 'Launch-Development.ps1')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'global.json') -Destination (Join-Path $fixture 'global.json')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Destination (Join-Path $fixture 'Directory.Build.props')
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType><UseWPF>true</UseWPF><RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <AssemblyName>MosaicTemplateStudio</AssemblyName><SelfContained>false</SelfContained>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath $project -Encoding UTF8
    $sentinel = Join-Path $fixture 'personal-project.mosaic'
    'Existing personal artwork must survive every launcher outcome.' | Set-Content -LiteralPath $sentinel -Encoding UTF8
    $sentinelHash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
    $userSentinels = @{}
    foreach ($relative in @('preferences.json', 'calibration.json', 'recovery/session.mosaic', 'image-cache/source.png')) {
        $path = Join-Path $fixtureData $relative
        [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
        [IO.File]::WriteAllText($path, ('Preserve existing launcher fixture data: ' + $relative))
        $userSentinels[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-DevelopmentShortcut.ps1') -Destination (Join-Path $fixtureScripts 'Install-DevelopmentShortcut.ps1')
    $fixtureIconDirectory = Join-Path $projectDirectory 'Assets'
    [IO.Directory]::CreateDirectory($fixtureIconDirectory) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'src/Mosaic.App/Assets/app.ico') -Destination (Join-Path $fixtureIconDirectory 'app.ico')
    'Preserve unrelated Desktop files.' | Set-Content -LiteralPath (Join-Path $shortcutDesktop 'personal-desktop-file.txt') -Encoding UTF8
    $personalDesktopHash = (Get-FileHash -LiteralPath (Join-Path $shortcutDesktop 'personal-desktop-file.txt') -Algorithm SHA256).Hash
    $shortcutName = 'Mosaic Studio ' + [char]0x2014 + ' Latest Build.lnk'
    $shortcutPath = Join-Path $shortcutDesktop $shortcutName
    & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixtureScripts 'Install-DevelopmentShortcut.ps1') -DesktopPath $shortcutDesktop *> (Join-Path $reportRoot 'shortcut-install.log')
    Check ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $shortcutPath)) 'shortcut installation supports Desktop and checkout paths containing spaces'
    $shortcutShell = New-Object -ComObject WScript.Shell
    try {
        $link = $shortcutShell.CreateShortcut($shortcutPath)
        try {
            Check ($link.TargetPath -eq $powershell) 'shortcut targets Windows PowerShell'
            $expectedArguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $fixtureScripts 'Launch-Development.ps1') + '"'
            Check ($link.Arguments -eq $expectedArguments -and $link.WindowStyle -eq 7) 'shortcut hides only the build helper and quotes the exact source launcher'
            Check ($link.WorkingDirectory -eq $fixture) 'shortcut working directory is the source checkout'
            Check ($link.IconLocation -eq ((Join-Path $fixtureIconDirectory 'app.ico') + ',0')) 'shortcut uses the application icon'
        }
        finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
    }
    finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcutShell) | Out-Null }
    & $powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixtureScripts 'Install-DevelopmentShortcut.ps1') -DesktopPath $shortcutDesktop *> (Join-Path $reportRoot 'shortcut-update.log')
    Check ($LASTEXITCODE -eq 0 -and @(Get-ChildItem -LiteralPath $shortcutDesktop -Filter '*.lnk').Count -eq 1) 'reinstall updates the same developer shortcut without duplicates'
    Check ((Get-FileHash -LiteralPath (Join-Path $shortcutDesktop 'personal-desktop-file.txt') -Algorithm SHA256).Hash -eq $personalDesktopHash) 'shortcut installation preserves unrelated Desktop files'
    Write-Fixture 'v1'
    # Only this child test process uses the bundled runtime for its framework-dependent WPF fixture.
    $env:DOTNET_ROOT = Split-Path -Parent $DotnetPath
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    & $DotnetPath restore $project -r win-x64 --artifacts-path $buildRoot -p:RestoreLockedMode=false *> (Join-Path $reportRoot 'fixture-restore.log')
    if ($LASTEXITCODE -ne 0) { throw "Fixture restore failed. See $reportRoot\fixture-restore.log" }

    Check ((Invoke-Launcher 'initial') -eq 0) 'first launch builds and starts successfully'
    $first = Read-StartedFixture 'v1'
    $firstId = $first.Id
    Check ((Invoke-Launcher 'already-running') -ne 0) 'a repeated launch refuses to rebuild an open development executable'
    $first.Refresh()
    Check (-not $first.HasExited -and (Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json).Pid -eq $firstId) 'running-instance refusal preserves the existing process'
    Check ((Get-Content -LiteralPath (Join-Path $reportRoot 'already-running.stderr.log') -Raw) -match 'Save your work and close its window') 'running-instance refusal gives explicit save-and-close guidance'
    $firstExecutable = $first.Path
    $firstBinaryHash = (Get-FileHash -LiteralPath $firstExecutable -Algorithm SHA256).Hash
    $firstBinaryTime = (Get-Item -LiteralPath $firstExecutable).LastWriteTimeUtc
    Stop-Fixture $first
    Check ((Invoke-Launcher 'unchanged-source') -eq 0) 'unchanged source starts successfully through the normal incremental pipeline'
    $unchanged = Read-StartedFixture 'v1'
    Check ((Get-FileHash -LiteralPath $firstExecutable -Algorithm SHA256).Hash -eq $firstBinaryHash -and (Get-Item -LiteralPath $firstExecutable).LastWriteTimeUtc -eq $firstBinaryTime) 'unchanged source reuses the existing binary without a clean or rewrite'
    Stop-Fixture $unchanged

    Write-Fixture 'v2'
    Check ((Invoke-Launcher 'changed-source') -eq 0) 'incremental launch builds changed source and starts successfully'
    $second = Read-StartedFixture 'v2'
    $executable = $second.Path
    Stop-Fixture $second
    $lastMarker = Get-Content -LiteralPath $marker -Raw
    'This deliberately invalid source must block launch.' | Set-Content -LiteralPath (Join-Path $projectDirectory 'Program.cs') -Encoding UTF8
    Check ((Invoke-Launcher 'compile-failure') -ne 0) 'a real compiler failure returns a nonzero launcher status'
    Check ((Test-Path -LiteralPath $executable) -and (Get-Content -LiteralPath $marker -Raw) -eq $lastMarker) 'build failure leaves the old executable present but does not launch it'
    $failureLog = Get-Content -LiteralPath (Join-Path $fixture 'artifacts/development/launcher.log') -Raw
    Check ($failureLog -match 'error CS') 'compiler diagnostics remain available in the actionable build log'
    @'
using System;
internal static class Program
{
    private static void Main() => Environment.Exit(23);
}
'@ | Set-Content -LiteralPath (Join-Path $projectDirectory 'Program.cs') -Encoding UTF8
    Check ((Invoke-Launcher 'startup-failure') -ne 0) 'an application that exits before showing a window fails the launch'
    $startupFailure = Get-Content -LiteralPath (Join-Path $fixture 'artifacts/development/launcher.log') -Raw
    Check ($startupFailure -match 'exit 23') 'startup failure retains the actionable application exit code'
    Check ((Get-Content -LiteralPath $marker -Raw) -eq $lastMarker) 'startup failure produces no new ready-editor marker'
    Check ((Invoke-Launcher 'missing-sdk' (Join-Path $fixture 'missing SDK/dotnet.exe')) -ne 0) 'an unavailable SDK fails without launching a previous executable'
    Check ((Get-Content -LiteralPath $marker -Raw) -eq $lastMarker) 'missing-SDK failure also leaves the prior launch marker unchanged'
    Check ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -eq $sentinelHash) 'existing project data survives successes and failures byte for byte'
    foreach ($path in $userSentinels.Keys) {
        Check ((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $userSentinels[$path]) ('isolated existing user-data sentinel is preserved: ' + $path.Substring($fixtureData.Length + 1))
    }
    [ordered]@{ Passed = $true; Checks = $checks; Fixture = $fixture; NativeInputInjected = $false; PersonalApplicationDataTouched = $false; AppDataEvidence = 'Isolated settings, calibration, recovery and image-cache sentinels remain byte-identical.'; GracefulFixtureClose = $true } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportRoot 'result.json') -Encoding UTF8
    Write-Output "PASS: $checks development-launcher checks. Evidence: $reportRoot"
}
finally {
    foreach ($process in $ownedProcesses) { Stop-Fixture $process }
    $env:DOTNET_ROOT = $oldDotnetRoot
    $env:DOTNET_ROOT_X64 = $oldDotnetRootX64
}
