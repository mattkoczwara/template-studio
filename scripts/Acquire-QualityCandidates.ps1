# Evaluation only. Does not change the application's bundled model or installer.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = (Resolve-Path "$PSScriptRoot/..").Path
function Get-Archive([string]$Url,[string]$Path,[string]$Hash,[string]$Destination) {
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    if (-not (Test-Path -LiteralPath $Path)) { Invoke-WebRequest $Url -OutFile $Path }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Hash) { throw "Evaluation archive integrity mismatch: $Path" }
    if (-not (Test-Path -LiteralPath $Destination)) { Expand-Archive -LiteralPath $Path -DestinationPath $Destination }
}
Get-Archive 'https://github.com/nihui/waifu2x-ncnn-vulkan/releases/download/20250915/waifu2x-ncnn-vulkan-20250915-windows.zip' (Join-Path $repo '.tools/ai/waifu2x.zip') '7425be94b94e4c8f37a1e433ac0e0100c43790e2c37418f4b65d8235adfbdc87' (Join-Path $repo '.tools/ai/extracted')
Get-Archive 'https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip' (Join-Path $repo '.tools/quality/realesrgan-20220424.zip') 'abc02804e17982a3be33675e4d471e91ea374e65b70167abc09e31acb412802d' (Join-Path $repo '.tools/quality/esrgan')
# The Met object45434 is marked Public Domain; its Open Access images use CC0.
# https://www.metmuseum.org/art/collection/search/45434
# https://www.metmuseum.org/hubs/open-access
$reference = Join-Path $repo '.tools/quality/met-45434.jpg'
if (-not (Test-Path -LiteralPath $reference)) { Invoke-WebRequest 'https://images.metmuseum.org/CRDImages/as/original/DP130155.jpg' -OutFile $reference }
if ((Get-FileHash -LiteralPath $reference -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'cbb9988f2f18b9180a1cc0cf5dbb0e3bbf8f2e85d17930cf6ca9ab36fac36303') { throw 'Met reference integrity mismatch' }
Write-Output 'Evaluation candidates ready. Run tests/Mosaic.Quality.Checks with the repository SDK; --fresh reruns every inference.'
