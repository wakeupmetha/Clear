# Сборка установщика: релизная публикация и упаковка Velopack.
#
#   Windows/pack.ps1
#
# Итог — dist\Releases\aerio-browser-win-Setup.exe и рядом переносная сборка
# (Portable.zip). Версия берётся из <Version> в App\App.csproj. Подписи кода нет:
# Windows при первом запуске установщика покажет предупреждение SmartScreen.
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
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
    dotnet vpk pack --packId aerio-browser --packTitle aerio --packAuthors aerio --packVersion $version `
        --packDir dist\app --mainExe aerio.exe --icon App\Assets\aerio.ico --framework webview2 `
        --runtime win-x64 --outputDir dist\Releases
    if ($LASTEXITCODE) { throw "pack failed" }
    Get-ChildItem dist\Releases | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
}
finally { Pop-Location }
