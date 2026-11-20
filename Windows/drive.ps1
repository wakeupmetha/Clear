# Проверка окна без мыши и клавиатуры человека: запуск тихого окна в своём
# тестовом мире, запрос в тест-канал, снимок только этого окна.
#
#   . Windows/drive.ps1
#   Start-Shell; Send-Bench @{ do = "history" }; Save-Shot "history"; Stop-Shell
#
# Команды тест-канала — в README, раздел «Тест-канал». Данные мира лежат в
# %LOCALAPPDATA%\aerio (shell); снимки — в $Shots.
Add-Type -AssemblyName System.Drawing.Common
Add-Type -Namespace Probe -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
[DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int how);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
'@
$script:Exe = Join-Path $PSScriptRoot "App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\aerio.exe"
$script:World = Join-Path $env:LOCALAPPDATA "aerio (shell)"
$script:Shots = Join-Path $env:TEMP "browser-shots"

function Start-Shell {
    $env:BROWSER_PROBE = "shell"
    # Окно не забирает фокус и не встаёт поверх окон человека.
    $env:BROWSER_QUIET = "1"
    $p = Start-Process -FilePath $script:Exe -PassThru
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) { $p.Refresh(); if ($p.HasExited -or $p.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 200 }
    if ($p.HasExited) { throw "shell exited with code $($p.ExitCode)" }
    $p
}

function Get-Shell { Get-Process -Name aerio -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $script:Exe } | Select-Object -First 1 }

function Save-Shot([string]$Name) {
    $p = Get-Shell
    if (-not $p) { throw "shell is not running" }
    [void][Probe.Win]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
    # Свёрнутое окно не рисуется: разворачиваем, не отбирая фокус у человека (4 = SW_SHOWNOACTIVATE).
    if ([Probe.Win]::IsIconic($p.MainWindowHandle)) { [void][Probe.Win]::ShowWindow($p.MainWindowHandle, 4); Start-Sleep -Milliseconds 900 }
    $r = New-Object Probe.Win+RECT
    [void][Probe.Win]::GetWindowRect($p.MainWindowHandle, [ref]$r)
    $bmp = [System.Drawing.Bitmap]::new($r.R - $r.L, $r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc(); [void][Probe.Win]::PrintWindow($p.MainWindowHandle, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
    [void](New-Item -ItemType Directory -Force $script:Shots)
    $out = Join-Path $script:Shots "$Name.png"
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    "$out ($($r.R - $r.L)x$($r.B - $r.T)), память процесса $([math]::Round($p.WorkingSet64 / 1MB)) МБ"
}

function Send-Bench([hashtable]$Request) {
    $c = [System.IO.Pipes.NamedPipeClientStream]::new(".", "aerio (shell).bench", [System.IO.Pipes.PipeDirection]::InOut)
    $c.Connect(5000)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Request | ConvertTo-Json -Compress -Depth 6) + "`n")
    $c.Write($bytes, 0, $bytes.Length); $c.Flush()
    $reader = [System.IO.StreamReader]::new($c)
    $line = $reader.ReadLine()
    $c.Dispose()
    $line
}

function Stop-Shell { $p = Get-Shell; if ($p) { [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(5000)) { Stop-Process -Id $p.Id -Force } } }

# Чистый мир: без вкладок, истории и настроек прошлой проверки.
function Reset-World { Stop-Shell; if (Test-Path $script:World) { Remove-Item -Recurse -Force $script:World -Confirm:$false } }
