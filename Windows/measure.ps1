# Замер расхода: память всех процессов (оболочка и движок WebView2), время
# запуска, открытие страницы, процессор в простое, размер программы. Тихое окно
# в своём мире, как у drive.ps1: окна человека и его фокус не трогаются.
#
#   Windows/measure.ps1 [-Program путь] [-Write]
#
# Без -Program — сборка из bin (Debug). Цифры, как у человека, — у упакованной:
# Portable.zip из pack.ps1, распакованный, current\aerio.exe. -Write дописывает
# строку в таблицу «Расход по версиям» в README (просьба владельца 2026-11-21:
# копить статистику).
#
# Страницы — свои, с локального сервера: сеть и чужие сайты не шумят в цифрах,
# и замеры разных версий сравнимы.
param([string]$Program, [switch]$Write)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\drive.ps1"
# Не $Exe: drive.ps1 при подключении перезаписывает эту переменную скрипта.
if ($Program) { $script:Exe = (Resolve-Path $Program).Path }
function B([hashtable]$r) { $o = Send-Bench $r | ConvertFrom-Json; if ($o -isnot [array] -and $o.error) { throw "$($r.do): $($o.error)" }; $o }

# Процесс оболочки и все его потомки: движок WebView2 — дерево msedgewebview2.exe.
function Tree {
    $root = (Get-Shell).Id
    $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, WorkingSetSize, PrivatePageCount
    $ids = [System.Collections.Generic.HashSet[uint32]]::new(); [void]$ids.Add($root)
    do { $grew = $false; foreach ($p in $all) { if ($ids.Contains($p.ParentProcessId) -and $ids.Add($p.ProcessId)) { $grew = $true } } } while ($grew)
    $mine = @($all | Where-Object { $ids.Contains($_.ProcessId) })
    [pscustomobject]@{
        Count = $mine.Count; Ids = @($mine.ProcessId)
        Private = [math]::Round(($mine | Measure-Object PrivatePageCount -Sum).Sum / 1MB)
        Set = [math]::Round(($mine | Measure-Object WorkingSetSize -Sum).Sum / 1MB)
        Shell = [math]::Round(($mine | Where-Object ProcessId -eq $root).PrivatePageCount / 1MB)
    }
}
function Say($t) { "$($t.Private) МБ ($($t.Count) проц.)" }

# Страница средней тяжести: текст, таблица, немного скрипта.
$site = Join-Path $env:TEMP "aerio-measure"
[void](New-Item -ItemType Directory -Force $site)
$rows = (1..300 | ForEach-Object { "<tr><td>$_</td><td>Row $_ of the table</td><td>$($_ * 7)</td></tr>" }) -join ""
$text = ("<p>" + ("Lorem ipsum dolor sit amet, consectetur adipiscing elit. " * 40) + "</p>") * 30
foreach ($n in 1..5) {
    Set-Content (Join-Path $site "page$n.html") "<!doctype html><meta charset=utf-8><title>Page $n</title><h1>Page $n</h1>$text<table>$rows</table><script>document.querySelectorAll('td').forEach((td, i) => td.dataset.i = i)</script>"
}
$server = Start-Process python -ArgumentList "-m", "http.server", "8767", "--bind", "127.0.0.1", "--directory", "`"$site`"" -PassThru -WindowStyle Hidden

try {
    # Прогрев: первый запуск после сборки в разы дольше (файлы проверяются).
    Reset-World; [void](Start-Shell); Start-Sleep 3; Stop-Shell

    Reset-World
    $clock = [Diagnostics.Stopwatch]::StartNew()
    [void](Start-Shell); $window = $clock.ElapsedMilliseconds
    while ($true) { try { [void](Send-Bench @{ do = "tabs" }); break } catch { Start-Sleep -Milliseconds 50 } }
    $ready = $clock.ElapsedMilliseconds
    Start-Sleep 5
    $empty = Tree

    # Загрузка — по часам самой страницы (navigation loadEventEnd): wait нарочно
    # ждёт ещё полсекунды тишины. Первая — от команды, с поднятием движка, без
    # этих полсекунд.
    $loads = @()
    $first = $null
    foreach ($n in 1..5) {
        $clock.Restart()
        $id = (B @{ do = "open"; url = "http://127.0.0.1:8767/page$n.html" }).id
        [void](B @{ do = "wait"; id = $id })
        if ($n -eq 1) { $cold = $clock.ElapsedMilliseconds - 500 }
        $loads += [math]::Round((B @{ do = "js"; id = $id; code = "performance.getEntriesByType('navigation')[0].loadEventEnd" }))
        if ($n -eq 1) { Start-Sleep 5; $first = Tree }
    }
    Start-Sleep 5
    $five = Tree

    # Процессор в простое: 10 с с пятью открытыми страницами, доля одного ядра.
    $cpu = { (Get-Process -Id (Tree).Ids -ErrorAction SilentlyContinue | Measure-Object CPU -Sum).Sum }
    $before = & $cpu; Start-Sleep 10; $after = & $cpu
    $idle = [math]::Round(($after - $before) / 10 * 100, 1)

    $clock.Restart(); Stop-Shell; $quit = $clock.ElapsedMilliseconds

    $version = (Get-Item $script:Exe).VersionInfo.ProductVersion -replace '\+.*$', ''
    $kind = if ($script:Exe -like "*\bin\*") { "Debug" } else { "Release" }
    $folder = [math]::Round((Get-ChildItem (Split-Path $script:Exe) -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
    $load = [math]::Round(($loads | Measure-Object -Average).Average)

    "версия:              $version ($kind)"
    "окно после запуска:  $window мс, канал готов: $ready мс"
    "пустое окно:         $(Say $empty), из них оболочка $($empty.Shell) МБ; рабочий набор $($empty.Set) МБ"
    "1 страница:          $(Say $first); рабочий набор $($first.Set) МБ"
    "5 страниц:           $(Say $five); рабочий набор $($five.Set) МБ"
    "первая страница:     $cold мс от команды, с поднятием движка"
    "загрузка страницы:   $($loads -join ', ') мс (часы страницы), в среднем $load мс"
    "процессор в простое: $idle% ядра"
    "закрытие:            $quit мс"
    "папка программы:     $folder МБ"

    if ($Write) {
        $readme = Join-Path $PSScriptRoot "README.md"
        $lines = [Collections.Generic.List[string]]([IO.File]::ReadAllText($readme) -split "`n")
        $head = $lines.FindIndex({ param($l) $l -like "| Дата | Версия |*" })
        if ($head -lt 0) { throw "README: нет таблицы «Расход по версиям»" }
        $end = $head; while ($end + 1 -lt $lines.Count -and $lines[$end + 1].StartsWith("|")) { $end++ }
        $date = Get-Date -Format "dd.MM.yyyy"
        $lines.Insert($end + 1, "| $date | $version | $kind | $window мс | $(Say $empty) | $(Say $first) | $(Say $five) | $cold / $load мс | $idle% | $folder МБ |")
        [IO.File]::WriteAllText($readme, ($lines -join "`n"))
        "строка дописана в README"
    }
}
finally {
    Stop-Shell
    Stop-Process -Id $server.Id -ErrorAction SilentlyContinue
}
