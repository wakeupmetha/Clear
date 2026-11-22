# Значок программы из логотипа: чёрный знак Clear на белой плитке со скруглением,
# как у оригинала. Плитка нужна: контур без неё терялся бы на тёмной панели задач.
#
#   pwsh Windows/App/Assets/icon.ps1      -> clear.ico рядом со скриптом
#
# Логотип — logo.svg (пустой контур пилюли). Рисует WPF: его разбор
# контура понимает запись SVG, поэтому сторонних программ не нужно.
param([string]$Preview)
Add-Type -AssemblyName PresentationCore, WindowsBase
$svg = [xml](Get-Content (Join-Path $PSScriptRoot "logo.svg") -Raw -Encoding UTF8)
$box = $svg.svg.viewBox -split ' ' | ForEach-Object { [double]::Parse($_, [cultureinfo]::InvariantCulture) }
$mark = [System.Windows.Media.Geometry]::Parse($svg.svg.g.path.d)

function Render([int]$size) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $pen = $visual.RenderOpen()
    $corner = $size * 0.22
    $pen.DrawRoundedRectangle([System.Windows.Media.Brushes]::White, $null, [System.Windows.Rect]::new(0, 0, $size, $size), $corner, $corner)
    # Знак занимает три четверти ширины плитки, как S у оригинала, и стоит по центру.
    $scale = $size * 0.75 / $box[2]
    $pen.PushTransform([System.Windows.Media.TranslateTransform]::new(($size - $box[2] * $scale) / 2, ($size - $box[3] * $scale) / 2))
    $pen.PushTransform([System.Windows.Media.ScaleTransform]::new($scale, $scale))
    $pen.DrawGeometry([System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x17, 0x17, 0x17)), $null, $mark)
    $pen.Pop(); $pen.Pop(); $pen.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $png = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $png.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $bytes = [IO.MemoryStream]::new()
    $png.Save($bytes)
    , $bytes.ToArray()
}

# Файл .ico: заголовок, по записи на размер, затем сами картинки в PNG.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = $sizes | ForEach-Object { , (Render $_) }
$out = [IO.MemoryStream]::new()
$w = [IO.BinaryWriter]::new($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $side = [byte]($sizes[$i] % 256)   # 256 записывается нулём
    $w.Write($side); $w.Write($side); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $w.Write($image) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "clear.ico"), $out.ToArray())
if ($Preview) { [IO.File]::WriteAllBytes($Preview, $images[-1]) }
"clear.ico: $($sizes.Count) размеров, $($out.Length) байт"
