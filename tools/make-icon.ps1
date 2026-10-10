#Requires -Version 7.0
<#
.SYNOPSIS
    元画像（PNG）から ManualVideoStudio のアプリアイコン Resources\icon.ico を生成する。

.DESCRIPTION
    Windows 標準と同じ構成の多重サイズ ICO を書き出す。
      - 16 / 20 / 24 / 32 / 40 / 48 / 64 px … 32bpp BMP（DIB）フレーム
      - 256 px                              … PNG フレーム
    20 / 40 は表示倍率 125%、24 / 48 は 150% のときに OS が要求するサイズ。

    Pillow の ICO 保存は全フレームを PNG にするが、小さいサイズの PNG フレームは
    読み込み側の実装によって扱いが揺れる。確実に読める BMP を小サイズに使う。

    Python / ImageMagick に依存させないため、PowerShell 7 の System.Drawing で処理する
    （PC ごとに Python の場所が違い、以前それで手順が再現できなかった）。

.PARAMETER Source
    元画像。正方形の PNG を想定。省略時は tools\icon-source.png。

.EXAMPLE
    pwsh -File tools\make-icon.ps1
    pwsh -File tools\make-icon.ps1 -Source C:\tmp\icon.png
#>
[CmdletBinding()]
param(
    [string]$Source
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $PSScriptRoot 'icon-source.png' }
if (-not (Test-Path $Source)) { throw "元画像が見つかりません: $Source" }
$Source = (Resolve-Path $Source).Path

$outputPath = Join-Path $projectRoot 'Resources\icon.ico'
$bmpSizes = 16, 20, 24, 32, 40, 48, 64
$pngSizes = , 256

function Resize-Image([System.Drawing.Image]$image, [int]$size) {
    # 一気に 1/50 まで縮めると GDI+ の bicubic でも細部が潰れるため、半分ずつ段階的に縮める。
    $current = [System.Drawing.Bitmap]::new($image)
    while ($current.Width / 2 -ge $size) {
        $next = New-ScaledBitmap $current ([int]($current.Width / 2))
        $current.Dispose()
        $current = $next
    }
    $result = New-ScaledBitmap $current $size
    $current.Dispose()
    return $result
}

function New-ScaledBitmap([System.Drawing.Bitmap]$bitmap, [int]$size) {
    $target = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($target)
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    try {
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        # 縁のピクセルが外側の透明と混ざって薄くなるのを防ぐ。
        $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $graphics.DrawImage($bitmap, [System.Drawing.Rectangle]::new(0, 0, $size, $size),
            0, 0, $bitmap.Width, $bitmap.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
    }
    finally {
        $attributes.Dispose()
        $graphics.Dispose()
    }
    return $target
}

function ConvertTo-DibBytes([System.Drawing.Bitmap]$bitmap) {
    $w = $bitmap.Width
    $h = $bitmap.Height
    $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
    $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = [Math]::Abs($data.Stride)
        $pixels = [byte[]]::new($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    }
    finally {
        $bitmap.UnlockBits($data)
    }

    $maskStride = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $xorSize = $w * 4 * $h
    $andSize = $maskStride * $h

    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    # BITMAPINFOHEADER。ICO 内の DIB は XOR + AND の 2 枚分として高さを 2 倍で書く決まり。
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]($xorSize + $andSize))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)

    # XOR（色）: BGRA、下の行から。
    for ($y = $h - 1; $y -ge 0; $y--) {
        $bw.Write($pixels, $y * $stride, $w * 4)
    }

    # AND（透過マスク）: 1bpp、下の行から。アルファ 0 の画素だけ 1（透明）にする。
    $row = [byte[]]::new($maskStride)
    for ($y = $h - 1; $y -ge 0; $y--) {
        [Array]::Clear($row)
        for ($x = 0; $x -lt $w; $x++) {
            if ($pixels[$y * $stride + $x * 4 + 3] -eq 0) {
                $row[[int][Math]::Floor($x / 8)] = $row[[int][Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8))
            }
        }
        $bw.Write($row)
    }

    $bw.Flush()
    return , $ms.ToArray()
}

function ConvertTo-PngBytes([System.Drawing.Bitmap]$bitmap) {
    $ms = [System.IO.MemoryStream]::new()
    $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

$sourceImage = [System.Drawing.Image]::FromFile($Source)
try {
    if ($sourceImage.Width -ne $sourceImage.Height) {
        Write-Warning ("元画像が正方形ではありません ({0}x{1})。縦横比は保たれません。" -f $sourceImage.Width, $sourceImage.Height)
    }

    $frames = foreach ($size in ($bmpSizes + $pngSizes | Sort-Object)) {
        $scaled = Resize-Image $sourceImage $size
        try {
            $isPng = $pngSizes -contains $size
            [pscustomobject]@{
                Size   = $size
                Format = if ($isPng) { 'PNG' } else { 'BMP' }
                Bytes  = if ($isPng) { ConvertTo-PngBytes $scaled } else { ConvertTo-DibBytes $scaled }
            }
        }
        finally {
            $scaled.Dispose()
        }
    }
}
finally {
    $sourceImage.Dispose()
}

$ms = [System.IO.MemoryStream]::new()
$bw = [System.IO.BinaryWriter]::new($ms)
# ICONDIR
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$frames.Count)
# ICONDIRENTRY × n。サイズ欄は 1 バイトなので 256 は 0 と書く。
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $dim = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]$frame.Bytes.Length); $bw.Write([int]$offset)
    $offset += $frame.Bytes.Length
}
foreach ($frame in $frames) { $bw.Write($frame.Bytes) }
$bw.Flush()

New-Item -ItemType Directory -Path (Split-Path $outputPath) -Force | Out-Null
[System.IO.File]::WriteAllBytes($outputPath, $ms.ToArray())

Write-Host "元画像: $Source"
foreach ($frame in $frames) {
    Write-Host ("  {0,3}px  {1}  {2,6:N0} bytes" -f $frame.Size, $frame.Format, $frame.Bytes.Length)
}
Write-Host ("完了: {0} ({1:N0} bytes)" -f $outputPath, (Get-Item $outputPath).Length)
Write-Host 'アイコンを変えたら obj\ と bin\ を消してから dotnet publish すること。'

