#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$DesktopPath = [Environment]::GetFolderPath('DesktopDirectory')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $PSScriptRoot 'Launch-Development.ps1'
$icon = Join-Path $repoRoot 'src/Mosaic.App/Assets/app.ico'
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) { throw "Development launcher not found: $launcher" }
if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) { throw "Application icon not found: $icon" }
if (-not (Test-Path -LiteralPath $DesktopPath -PathType Container)) { throw "Desktop folder not found: $DesktopPath" }
$powershell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
# Construct the dash explicitly so Windows PowerShell 5.1 can read this ASCII script.
$name = 'Mosaic Studio ' + [char]0x2014 + ' Latest Build'
$shortcutPath = Join-Path $DesktopPath ($name + '.lnk')
$shell = New-Object -ComObject WScript.Shell
try {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $powershell
    $shortcut.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $launcher + '"'
    $shortcut.WorkingDirectory = $repoRoot
    $shortcut.IconLocation = $icon + ',0'
    $shortcut.Description = 'Build current Release sources, then open Mosaic Studio. Development checkout only.'
    $shortcut.WindowStyle = 7
    $shortcut.Save()
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
}
finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
Write-Output "Created: $shortcutPath"
