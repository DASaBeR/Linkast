# SPDX-License-Identifier: GPL-3.0-or-later
<#
.PARAMETER WriteAppIcon
  Also regenerate src/Linkast.App/Assets/linkast.ico and linkast-256.png from Brand.xaml. Off by
  default: the shipped app icon is hand-made artwork placed in Assets by the maintainer, and this
  script must never overwrite it silently.

.SYNOPSIS
  Renders Linkast's brand assets from src/Linkast.App/Themes/Brand.xaml, the single source of
  the logo, so the app, its icon and the docs always match:
    src/Linkast.App/Assets/linkast.ico        app icon, 16-256 px
    src/Linkast.App/Assets/linkast-256.png    app icon as PNG (installer, docs)
    docs/assets/linkast-mark.png              the mark on transparent background
    docs/assets/linkast-logo-dark.png         mark + "Linkast" + tagline, for dark backgrounds
    docs/assets/linkast-logo-light.png        the same, for light backgrounds
  Run again after editing Brand.xaml.
#>
param([switch]$WriteAppIcon)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Xaml
$repo = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $repo "src\Linkast.App\Assets"
$docs = Join-Path $repo "docs\assets"
New-Item -ItemType Directory -Force $assets, $docs | Out-Null

$xaml = Get-Content (Join-Path $repo "src\Linkast.App\Themes\Brand.xaml") -Raw
$brand = [System.Windows.Markup.XamlReader]::Parse($xaml)

function Render([int]$width, [int]$height, [scriptblock]$draw) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    & $draw $dc
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $width, $height, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return , $ms.ToArray()
}

function Text([string]$s, [double]$size, [string]$weight, $brush, [double]$spacing = 0) {
    $face = New-Object System.Windows.Media.Typeface (New-Object System.Windows.Media.FontFamily "Segoe UI Variable Display, Segoe UI"), ([System.Windows.FontStyles]::Normal), ([System.Windows.FontWeights]::$weight), ([System.Windows.FontStretches]::Normal)
    if ($spacing -gt 0) { $s = ($s.ToCharArray() -join [char]0x200A) }
    return New-Object System.Windows.Media.FormattedText $s, ([Globalization.CultureInfo]::InvariantCulture), ([System.Windows.FlowDirection]::LeftToRight), $face, $size, $brush, 1.0
}

# App icon at every Windows icon size; small sizes get the same art (it stays legible at 16 px).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$appIcon = $brand["BrandAppIcon"]
$images = foreach ($s in $sizes) {
    , (Render $s $s { param($dc) $dc.DrawImage($appIcon, (New-Object System.Windows.Rect 0, 0, $s, $s)) })
}

if ($WriteAppIcon) {
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
[IO.File]::WriteAllBytes((Join-Path $assets "linkast.ico"), $out.ToArray())
[IO.File]::WriteAllBytes((Join-Path $assets "linkast-256.png"), $images[-1])
}

# The mark alone, 4x the design box.
$mark = $brand["BrandMark"]
[IO.File]::WriteAllBytes((Join-Path $docs "linkast-mark.png"), (Render 968 760 { param($dc) $dc.DrawImage($mark, (New-Object System.Windows.Rect 0, 0, 968, 760)) }))

# Horizontal lockup: mark, wordmark and tagline.
foreach ($variant in @(@{ Name = "dark"; Word = "#FFFFFFFF"; Tag = "#FFC8CCE0" }, @{ Name = "light"; Word = "#FF0B0D24"; Tag = "#FF3A3F5C" })) {
    $wordBrush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($variant.Word))
    $tagBrush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($variant.Tag))
    $png = Render 1200 300 {
        param($dc)
        $dc.DrawImage($mark, (New-Object System.Windows.Rect 30, 40, 280, 220))
        $word = Text "Linkast" 170 "SemiBold" $wordBrush
        $dc.DrawText($word, (New-Object System.Windows.Point 360, 10))
        $dot = [string][char]0x2022  # written as a code: Windows PowerShell reads scripts as ANSI
        $tag = Text "STREAM  $dot  CONNECT  $dot  CREATE" 34 "Normal" $tagBrush 1
        $dc.DrawText($tag, (New-Object System.Windows.Point 368, 222))
    }
    [IO.File]::WriteAllBytes((Join-Path $docs "linkast-logo-$($variant.Name).png"), $png)
}

# Linkast Camera placeholder (1280 x 720), shown to Zoom, Teams and others while Linkast isn't sending.
$navyTop = [System.Windows.Media.ColorConverter]::ConvertFromString("#FF1D1B48")
$navyBottom = [System.Windows.Media.ColorConverter]::ConvertFromString("#FF090A1C")
$white = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Colors]::White)
$grey = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString("#FFB4B9D6"))
$placeholder = Render 1280 720 {
    param($dc)
    $bg = New-Object System.Windows.Media.LinearGradientBrush $navyTop, $navyBottom, 90
    $dc.DrawRectangle($bg, $null, (New-Object System.Windows.Rect 0, 0, 1280, 720))
    # The maintainer's logo artwork (used as supplied, never modified).
    $logo = New-Object System.Windows.Media.Imaging.BitmapImage (New-Object Uri (Join-Path $assets "linkast-logo.png"))
    $dc.DrawImage($logo, (New-Object System.Windows.Rect 520, 110, 240, 240))
    foreach ($line in @(@{ Text = "Linkast is not running"; Size = 46; Weight = "SemiBold"; Brush = $white; Y = 395 }, @{ Text = "Open Linkast to use this camera"; Size = 28; Weight = "Normal"; Brush = $grey; Y = 465 })) {
        $t = Text $line.Text $line.Size $line.Weight $line.Brush
        $dc.DrawText($t, (New-Object System.Windows.Point ((1280 - $t.Width) / 2), $line.Y))
    }
}
[IO.File]::WriteAllBytes((Join-Path $repo "native\Linkast.VirtualCamera\placeholder.png"), $placeholder)

Write-Host "Brand assets written to $assets, $docs and native\Linkast.VirtualCamera"
