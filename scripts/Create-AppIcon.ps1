$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$target = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Mosaic.App/Assets/app.ico'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
$images = @()
foreach ($size in @(32,48,256)) {
    $bitmap = [Drawing.Bitmap]::new($size,$size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([Drawing.Color]::Transparent)
        $gap = [Math]::Max(1,[int]($size/24))
        $cell = [int](($size-3*$gap)/2)
        $colors = @('#286B5C','#B6CBB5','#D3AF79','#6F9481')
        for ($i=0;$i -lt 4;$i++) {
            $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($colors[$i]))
            try { $graphics.FillRectangle($brush,($gap+($i%2)*($cell+$gap)),($gap+[int][Math]::Floor($i/2)*($cell+$gap)),$cell,$cell) }
            finally { $brush.Dispose() }
        }
        $buffer = [IO.MemoryStream]::new()
        try { $bitmap.Save($buffer,[Drawing.Imaging.ImageFormat]::Png); $images += ,$buffer.ToArray() }
        finally { $buffer.Dispose() }
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
$stream = [IO.File]::Create($target)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]3)
    $offset = 6+16*3
    $sizes = @(32,48,256)
    for ($i=0;$i -lt 3;$i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
} finally { $writer.Dispose(); $stream.Dispose() }
Write-Output "Created $target"
