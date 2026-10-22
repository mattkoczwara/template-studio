[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$checked = 0
$files = @((Join-Path $root 'README.md'), (Join-Path $root 'CONTRIBUTING.md'), (Join-Path $root 'AGENTS.md'), (Join-Path $root 'THIRD-PARTY-NOTICES.md'), (Join-Path $root 'CHANGELOG.md'))
$files += @(Get-ChildItem (Join-Path $root 'docs') -File -Filter '*.md' | ForEach-Object FullName)
foreach ($file in $files) {
    $content = Get-Content -LiteralPath $file -Raw
    $links = [regex]::Matches($content, '(?:\]\(|(?:src|href)=")([^)"\s]+)')
    foreach ($link in $links) {
        $target = $link.Groups[1].Value
        if ($target -match '^(https?://|mailto:|#)') { continue }
        $relative = [Uri]::UnescapeDataString(($target -split '#')[0])
        if (-not $relative) { continue }
        $path = Join-Path (Split-Path -Parent $file) $relative
        if (-not (Test-Path -LiteralPath $path)) { throw "Broken local link in $file : $target" }
        $checked++
    }
}
$aiRoot = Join-Path $root 'vendor/ai'
foreach ($asset in (Get-Content (Join-Path $aiRoot 'assets.json') -Raw | ConvertFrom-Json)) {
    $path = [IO.Path]::GetFullPath((Join-Path $aiRoot $asset.path))
    if (-not $path.StartsWith([IO.Path]::GetFullPath($aiRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'AI path escapes vendor directory.' }
    if ((Get-Item -LiteralPath $path).Length -ne $asset.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) { throw "AI integrity mismatch: $($asset.path)" }
}
[xml](Get-Content (Join-Path $root 'docs/assets/banner.svg') -Raw) | Out-Null
Write-Output "PASS: $checked local documentation/asset links; pinned AI payload hashes; banner XML."
