# Значок программы из логотипа: белый знак aerio на чёрной плитке со скруглением.
# Белый знак без плитки пропал бы на светлой панели задач и в проводнике.
#
#   pwsh Windows/App/Assets/icon.ps1      -> aerio.ico рядом со скриптом
#
# Логотип — logo.svg (копия White-2d.svg владельца). Рисует WPF: его разбор
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
    $pen.DrawRoundedRectangle([System.Windows.Media.Brushes]::Black, $null, [System.Windows.Rect]::new(0, 0, $size, $size), $corner, $corner)
    # Знак занимает 70% ширины плитки и стоит по центру.
    $scale = $size * 0.70 / $box[2]
    $pen.PushTransform([System.Windows.Media.TranslateTransform]::new(($size - $box[2] * $scale) / 2, ($size - $box[3] * $scale) / 2))
    $pen.PushTransform([System.Windows.Media.ScaleTransform]::new($scale, $scale))
    $pen.DrawGeometry([System.Windows.Media.Brushes]::White, $null, $mark)
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
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "aerio.ico"), $out.ToArray())
if ($Preview) { [IO.File]::WriteAllBytes($Preview, $images[-1]) }
"aerio.ico: $($sizes.Count) размеров, $($out.Length) байт"
