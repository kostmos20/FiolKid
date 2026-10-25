# Рисует значок FiolKid (фиолетовый круг, белая кнопка питания и искорка) и собирает .ico из PNG
# нескольких размеров. Запуск: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
# Результат: src\ZapretPro.App\Assets\fiolkid.ico и fiolkid-256.png (для окна «О программе» и превью).
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\ZapretPro.App\Assets'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 256.0
    $pad = [Math]::Max(1, 8 * $s)
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)

    # Фон: фиолетовый градиент сверху-слева вниз-вправо
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0xB3, 0x8C, 0xFF)), ([System.Drawing.Color]::FromArgb(255, 0x4C, 0x1D, 0x95)), 45.0
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @([System.Drawing.Color]::FromArgb(255, 0xC4, 0xA1, 0xFF), [System.Drawing.Color]::FromArgb(255, 0x7C, 0x3A, 0xED), [System.Drawing.Color]::FromArgb(255, 0x3B, 0x14, 0x7A))
    $blend.Positions = @(0.0, 0.55, 1.0)
    $bg.InterpolationColors = $blend
    $g.FillEllipse($bg, $rect)

    # Блик сверху — объём
    if ($size -ge 32) {
        $glossRect = New-Object System.Drawing.RectangleF ($pad + 22 * $s), ($pad + 10 * $s), ($size - 2 * $pad - 44 * $s), (110 * $s)
        $gloss = New-Object System.Drawing.Drawing2D.LinearGradientBrush $glossRect, ([System.Drawing.Color]::FromArgb(90, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90.0
        $g.FillEllipse($gloss, $glossRect)
    }

    # Кнопка питания: дуга и палочка
    $w = [Math]::Max(1.6, 22 * $s)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $w
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $c = $size / 2.0
    $r = 62 * $s
    $arc = New-Object System.Drawing.RectangleF ($c - $r), ($c - $r + 10 * $s), (2 * $r), (2 * $r)
    $g.DrawArc($pen, $arc, -50, 280)
    $g.DrawLine($pen, [single]$c, [single]($c - 72 * $s), [single]$c, [single]($c - 4 * $s))

    # Искорка справа сверху — «Kid»: весёлая, не строгая
    if ($size -ge 24) {
        $sx = $c + 78 * $s; $sy = $c - 78 * $s; $big = 26 * $s; $small = 8 * $s
        $pts = @(
            (New-Object System.Drawing.PointF $sx, ($sy - $big)),
            (New-Object System.Drawing.PointF ($sx + $small), ($sy - $small)),
            (New-Object System.Drawing.PointF ($sx + $big), $sy),
            (New-Object System.Drawing.PointF ($sx + $small), ($sy + $small)),
            (New-Object System.Drawing.PointF $sx, ($sy + $big)),
            (New-Object System.Drawing.PointF ($sx - $small), ($sy + $small)),
            (New-Object System.Drawing.PointF ($sx - $big), $sy),
            (New-Object System.Drawing.PointF ($sx - $small), ($sy - $small))
        )
        $spark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xFD, 0xE6, 0x8A))
        $g.FillPolygon($spark, $pts)
    }

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($size in $sizes) {
    $bmp = New-Frame $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bmp.Save((Join-Path $OutDir 'fiolkid-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    $pngs += , @($size, $ms.ToArray())
}

# ICO: заголовок, каталог, затем PNG-картинки подряд
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $size = $p[0]; $data = $p[1]
    $dim = if ($size -ge 256) { 0 } else { $size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'fiolkid.ico'), $out.ToArray())
Write-Host "Готово: $(Join-Path $OutDir 'fiolkid.ico')"
