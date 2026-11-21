# Сборка установщика: релизная публикация и упаковка Velopack.
#
#   Windows/pack.ps1
#
# Итог — dist\Releases\aerio-browser-win-Setup.exe и рядом переносная сборка
# (Portable.zip). Версия берётся из <Version> в App\App.csproj. Подписи кода нет:
# Windows при первом запуске установщика покажет предупреждение SmartScreen.
#
#   Windows/pack.ps1 -Bump
#
# сначала поднимает последнюю цифру версии в App\App.csproj: так собирает
# обновление изнутри приложения (App/Updater.cs) — Velopack ставит только версию
# выше установленной.
param([switch]$Bump)
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    if ($Bump) {
        # Через .NET, а не Get-Content: Windows PowerShell читает файл без BOM как
        # ANSI и записал бы русские комментарии кракозябрами. Без BOM и с теми же
        # концами строк: файл меняется только в версии.
        $file = (Resolve-Path App\App.csproj).Path
        $project = [IO.File]::ReadAllText($file)
        $old = [regex]::Match($project, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
        if (-not $old.Success) { throw "no <Version>x.y.z</Version> in App.csproj" }
        $new = "$($old.Groups[1]).$($old.Groups[2]).$([int]$old.Groups[3].Value + 1)"
        [IO.File]::WriteAllText($file, $project.Replace($old.Value, "<Version>$new</Version>"))
        "version: $new"
    }
    $version = ([xml](Get-Content App\App.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    # ponytail: прошлые выпуски стираются, поэтому каждый выпуск полный. Когда
    # появится источник обновлений, dist\Releases надо хранить: из него vpk
    # делает разностные пакеты.
    foreach ($old in "dist\app", "dist\Releases") { if (Test-Path $old) { Remove-Item -Recurse -Force $old -Confirm:$false } }
    dotnet publish App -c Release -p:Platform=x64 -r win-x64 --self-contained true -o dist\app
    if ($LASTEXITCODE) { throw "publish failed" }
    # Имя пакета не «aerio»: Velopack ставит программу в %LOCALAPPDATA%\<имя пакета>
    # и при удалении стирает эту папку целиком, а в %LOCALAPPDATA%\aerio лежат
    # вкладки, история и входы человека. --framework: на Windows 10 без движка
    # WebView2 установщик сам его поставит.
    # Чего в пакете нет (задача 13: установщик 108 → 83 МБ, программа 259 → 199 МБ):
    # родных библиотек ИИ, поиска и виджетов из Windows App SDK — onnxruntime и
    # DirectML (40 МБ), Microsoft.Windows.AI.*, Search, SemanticIndex, Widgets,
    # Graphics.Imaging: браузер их не вызывает, а грузятся они, только когда их
    # вызвали. Управляемые обёртки к ним остаются: они записаны в aerio.deps.json,
    # без них не запустится. Ещё нет чтения pdb (pdb не кладём, это и умолчание
    # vpk) и переводов элементов WinUI, кроме английских: надписи окна — на
    # английском, без перевода элемент берёт английский текст из своей библиотеки.
    # ponytail: имена списком; новая версия Windows App SDK может принести новую
    # библиотеку ИИ — она просто попадёт в пакет. Смотреть крупнейшие файлы dist\app.
    $exclude = '\.pdb$' +
        '|(^|[\\/])(onnxruntime|DirectML|Microsoft\.DiaSymReader\.Native\.amd64' +
        '|Microsoft\.Windows\.AI\.(Imaging|MachineLearning|Text|Search\.Experimental|Video|AICapabilities|ContentSafety)' +
        '|Microsoft\.Windows\.Search|Microsoft\.Asg\.SemanticIndex\.AiFabric\.Compatibility|Microsoft\.Windows\.Widgets|Microsoft\.Graphics\.Imaging)\.dll$' +
        '|(^|[\\/])(?!en-us[\\/]|en-GB[\\/])[A-Za-z]{2,3}(-[A-Za-z0-9]+)+[\\/][^\\/]+\.mui$'
    dotnet vpk pack --packId aerio-browser --packTitle aerio --packAuthors aerio --packVersion $version `
        --packDir dist\app --mainExe aerio.exe --icon App\Assets\aerio.ico --framework webview2 `
        --runtime win-x64 --outputDir dist\Releases --exclude $exclude
    if ($LASTEXITCODE) { throw "pack failed" }
    Get-ChildItem dist\Releases | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
}
finally { Pop-Location }
