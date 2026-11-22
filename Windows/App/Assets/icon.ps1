# Значок программы из логотипа владельца: logo.svg — уже готовый значок (белая
# плитка со скруглением, чёрная пилюля, белая C), его только переводим в .ico.
#
#   pwsh Windows/App/Assets/icon.ps1      -> clear.ico рядом со скриптом
#
# Рисует WPF: прямоугольники (rect, скругление rx) и контуры (path) — каждый своей
# заливкой, по порядку, как в файле. Разбор контура WPF понимает запись SVG,
# поэтому сторонних программ не нужно.
# ponytail: другие элементы SVG (circle, g, transform, stroke) не рисуются;
# понадобятся — переписать их в path или добавить сюда.
param([string]$Preview)
Add-Type -AssemblyName PresentationCore, WindowsBase
$svg = [xml](Get-Content (Join-Path $PSScriptRoot "logo.svg") -Raw -Encoding UTF8)
$invariant = [cultureinfo]::InvariantCulture
$box = $svg.svg.viewBox -split ' ' | ForEach-Object { [double]::Parse($_, $invariant) }
function Number($value) { if ($value) { [double]::Parse($value, $invariant) } else { 0 } }
function Fill($colour) { [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString($colour)) }

function Render([int]$size) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $pen = $visual.RenderOpen()
    $pen.PushTransform([System.Windows.Media.ScaleTransform]::new($size / $box[2], $size / $box[3]))
    foreach ($node in $svg.svg.ChildNodes) {
        switch ($node.LocalName) {
            "rect" {
                $round = Number $node.rx
                $frame = [System.Windows.Rect]::new((Number $node.x), (Number $node.y), (Number $node.width), (Number $node.height))
                $pen.DrawRoundedRectangle((Fill $node.fill), $null, $frame, $round, $round)
            }
            "path" { $pen.DrawGeometry((Fill $node.fill), $null, [System.Windows.Media.Geometry]::Parse($node.d)) }
        }
    }
    $pen.Pop(); $pen.Close()
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
