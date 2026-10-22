#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$DotnetPath,
    [switch]$NoDialogs
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/Mosaic.App/Mosaic.App.csproj'
$artifacts = Join-Path $repoRoot 'artifacts/development'
$buildRoot = Join-Path $artifacts 'build'
$log = Join-Path $artifacts 'launcher.log'
$launchMutex = $null
$ownsMutex = $false
$locationPushed = $false

function Write-LauncherLog([string]$Message) {
    Add-Content -LiteralPath $log -Value $Message -Encoding UTF8
}

function Invoke-DevelopmentSdk([string[]]$SdkArguments) {
    $stdout = Join-Path $artifacts 'sdk.stdout.log'
    $stderr = Join-Path $artifacts 'sdk.stderr.log'
    Write-LauncherLog ("dotnet " + ($SdkArguments -join ' '))
    # Start-Process joins ArgumentList on Windows PowerShell. Quote each argument,
    # including MSBuild properties whose checkout paths may contain spaces.
    $quoted = $SdkArguments | ForEach-Object { '"' + $_ + '"' }
    $sdk = Start-Process -FilePath $DotnetPath -ArgumentList $quoted -WorkingDirectory $repoRoot `
        -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $output = [IO.File]::ReadAllText($stdout)
    Write-LauncherLog $output
    Write-LauncherLog ([IO.File]::ReadAllText($stderr))
    if ($sdk.ExitCode -ne 0) { throw "The .NET build command failed (exit $($sdk.ExitCode)). No previous executable was launched." }
    return $output
}

try {
    [IO.Directory]::CreateDirectory($artifacts) | Out-Null
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $identity = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($repoRoot.ToUpperInvariant()))).Replace('-', '') }
    finally { $hash.Dispose() }
    $launchMutex = [Threading.Mutex]::new($false, ('Local\MosaicStudioDevelopment-' + $identity))
    try { $ownsMutex = $launchMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another launch is building or starting this checkout. Wait for it to finish before clicking again.' }
    Set-Content -LiteralPath $log -Value ("Mosaic development launch - " + [DateTimeOffset]::Now.ToString('O')) -Encoding UTF8
    Push-Location -LiteralPath $repoRoot
    $locationPushed = $true
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Application project is missing: $project. Restore the checkout, then retry." }
    if (-not $DotnetPath) {
        $localSdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
        $DotnetPath = if (Test-Path -LiteralPath $localSdk -PathType Leaf) { $localSdk } else { (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source }
    }
    if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) { throw "The .NET SDK executable was not found: $DotnetPath." }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/cli'
    $env:NUGET_PACKAGES = Join-Path $repoRoot '.tools/packages'
    $properties = @('-p:Configuration=Release', '-p:RuntimeIdentifier=win-x64', '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$buildRoot")
    # Ask MSBuild for the target instead of guessing the SDK's output directory.
    $target = (Invoke-DevelopmentSdk (@('msbuild', $project, '-nologo', '-getProperty:TargetPath') + $properties)).Trim()
    if (-not [IO.Path]::IsPathRooted($target)) { throw 'MSBuild did not return an absolute application target path. Check the project configuration.' }
    $executable = [IO.Path]::ChangeExtension($target, '.exe')
    foreach ($running in @(Get-Process -Name 'MosaicTemplateStudio' -ErrorAction SilentlyContinue)) {
        if ([string]::Equals($running.Path, $executable, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'This development build is already open. Save your work and close its window, then click the shortcut again to build the latest sources.'
        }
    }
    # An ordinary incremental Release build: no clean, publish, installer, or data reset.
    $null = Invoke-DevelopmentSdk (@('build', $project, '-nologo', '-p:RestoreLockedMode=true') + $properties)
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Build completed without its application executable: $executable." }
    Write-LauncherLog ("Launching: $executable`r`nWorking directory: $repoRoot")
    $appStdout = Join-Path $artifacts 'startup.stdout.log'
    $appStderr = Join-Path $artifacts 'startup.stderr.log'
    # The requested interactive WPF window opens normally; only build helpers are hidden.
    $application = Start-Process -FilePath $executable -WorkingDirectory $repoRoot -WindowStyle Normal `
        -PassThru -RedirectStandardOutput $appStdout -RedirectStandardError $appStderr
    # Cache the handle before a very early exit; Windows PowerShell otherwise
    # can return an empty ExitCode from a detached Start-Process result.
    $null = $application.Handle
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 100
        $application.Refresh()
        if ($application.HasExited) {
            $application.WaitForExit()
            Write-LauncherLog ([IO.File]::ReadAllText($appStderr))
            throw "The application exited before showing its editor (exit $($application.ExitCode)). See startup.stderr.log beside the launch log."
        }
        if ($application.MainWindowHandle -ne [IntPtr]::Zero -and -not [string]::IsNullOrWhiteSpace($application.MainWindowTitle)) {
            Write-LauncherLog ("Editor opened. PID: " + $application.Id + "`r`nWindow: " + $application.MainWindowTitle)
            Write-Output ("Mosaic Studio latest build opened (PID " + $application.Id + ').')
            exit 0
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The editor did not appear within 15 seconds. It may still be starting; check startup.stderr.log and any Windows error dialog. Save and close it before retrying.'
}
catch {
    $message = $_.Exception.Message + "`r`n`r`nUse the .NET SDK pinned in global.json. For compiler errors, fix the reported source and retry; for locked files, save and close the development editor. The first package restore needs Internet access.`r`n`r`nLog: $log"
    try {
        Write-LauncherLog ("FAILED: " + $_.Exception.Message)
        $tail = @(Get-Content -LiteralPath $log -Tail 12)
        $message += "`r`n`r`n" + ($tail -join "`r`n")
    }
    catch { } # A log-write failure must not suppress the original error.
    [Console]::Error.WriteLine($message)
    if (-not $NoDialogs) {
        try {
            Add-Type -AssemblyName PresentationFramework
            [System.Windows.MessageBox]::Show($message, 'Mosaic Studio - Latest Build', 'OK', 'Error') | Out-Null
        }
        catch { [Console]::Error.WriteLine('Could not show the error dialog. Read the launch log above.') }
    }
    exit 1
}
finally {
    if ($locationPushed) { Pop-Location }
    if ($ownsMutex) { $launchMutex.ReleaseMutex() }
    if ($null -ne $launchMutex) { $launchMutex.Dispose() }
}
