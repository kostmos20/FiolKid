# Готовит картинки зимнего оформления (их сгенерировал Codex) для программы: уменьшает до размера окна
# с запасом для чётких экранов и делает приглушённую копию кнопки для выключенного состояния.
# Запуск: powershell -ExecutionPolicy Bypass -File tools\prepare-winter-assets.ps1 -Source <папка с PNG>
param([Parameter(Mandatory)][string]$Source)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\src\ZapretPro.App\Assets\winter'
New-Item -ItemType Directory -Force $out | Out-Null

function Resize([string]$name, [string]$target, [int]$width, [string]$format = 'png', [single]$saturation = 1, [single]$brightness = 1) {
    $src = [System.Drawing.Image]::FromFile((Join-Path $Source $name))
    $height = [int][Math]::Round($src.Height * $width / $src.Width)
    $bmp = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $attrs = New-Object System.Drawing.Imaging.ImageAttributes
    if ($saturation -ne 1 -or $brightness -ne 1) {
        # Приглушить цвет (как CSS grayscale) и чуть затемнить (brightness)
        $lr = 0.299; $lg = 0.587; $lb = 0.114; $s = $saturation; $b = $brightness
        $m = New-Object System.Drawing.Imaging.ColorMatrix
        $m.Matrix00 = ($lr * (1 - $s) + $s) * $b; $m.Matrix01 = $lr * (1 - $s) * $b; $m.Matrix02 = $lr * (1 - $s) * $b
        $m.Matrix10 = $lg * (1 - $s) * $b; $m.Matrix11 = ($lg * (1 - $s) + $s) * $b; $m.Matrix12 = $lg * (1 - $s) * $b
        $m.Matrix20 = $lb * (1 - $s) * $b; $m.Matrix21 = $lb * (1 - $s) * $b; $m.Matrix22 = ($lb * (1 - $s) + $s) * $b
        $m.Matrix33 = 1; $m.Matrix44 = 1
        $attrs.SetColorMatrix($m)
    }
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $width, $height), 0, 0, $src.Width, $src.Height, 'Pixel', $attrs)
    $path = Join-Path $out $target
    if ($format -eq 'jpg') {
        $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
        $params = New-Object System.Drawing.Imaging.EncoderParameters 1
        $params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 88L
        $bmp.Save($path, $codec, $params)
    } else {
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $g.Dispose(); $bmp.Dispose(); $src.Dispose()
    '{0}  {1}×{2}  {3:N0} КБ' -f $target, $width, $height, ((Get-Item $path).Length / 1KB)
}

Resize 'winter-street.png' 'background.jpg' 1000 'jpg'
Resize 'service-block.png' 'card-off.png' 696
Resize 'youtube-on.png' 'card-youtube.png' 696
Resize 'discord-on.png' 'card-discord.png' 696
Resize 'telegram-on.png' 'card-telegram.png' 696
Resize 'vpn-on.png' 'card-vpn.png' 696
Resize 'power-button.png' 'button-on.png' 708
# Выключено: grayscale(.65) brightness(.88), как в образце
Resize 'power-button.png' 'button-off.png' 708 'png' 0.35 0.88
Resize 'snow-cap.png' 'snow-cap.png' 1044
