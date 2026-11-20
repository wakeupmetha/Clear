using System.Diagnostics;
using System.Reflection;
using System.Text;
using Velopack;

namespace Browser;

/// Обновление изнутри приложения: собрать aerio заново из исходников на этом
/// компьютере (pack.ps1 -Bump — версия на шаг выше) и поставить собранное поверх
/// себя (Velopack из dist\Releases), с перезапуском. Решение владельца
/// 2026-11-21: «Пересборка здесь». В апстриме — «Check for Updates…» с сервера.
// ponytail: исходники берутся как есть, без git pull; понадобится тянуть чужие
// правки — добавить его перед сборкой.
static class Updater
{
    /// Папка Windows/ исходников, из которой собрана эта программа (её кладёт
    /// сборка, App.csproj); null — исходников на этом компьютере нет.
    public static string? Source { get; } = Find();

    /// Версия этой программы, без хвоста сборки.
    public static string Version { get; } =
        (typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "").Split('+')[0];

    /// Поставлена ли программа установщиком (или переносная из него): только
    /// такую Velopack умеет обновить. Сборка из bin — нет.
    public static bool Installed => Source is not null && new UpdateManager(Releases).IsInstalled;

    /// Идёт ли сборка: страницу настроек можно закрыть и открыть заново.
    public static bool Busy { get; private set; }

    static string Releases => Path.Combine(Source ?? "", "dist", "Releases");

    static string? Find()
    {
        var folder = typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(meta => meta.Key == "Source")?.Value;
        return folder is { Length: > 0 } && File.Exists(Path.Combine(folder, "pack.ps1")) ? folder : null;
    }

    /// Собрать заново и поставить, потом quit закрывает окно как обычно:
    /// установщик ждёт выхода, ставит и запускает новую версию.
    public static async Task Update(Action quit)
    {
        if (Busy || Source is null) return;
        Busy = true;
        try
        {
            await Rebuild();
            var updates = new UpdateManager(Releases);
            var next = await updates.CheckForUpdatesAsync() ?? throw new InvalidOperationException("The new build isn't newer than this one");
            await updates.DownloadUpdatesAsync(next);
            updates.WaitExitThenApplyUpdates(next.TargetFullRelease, silent: true, restart: true);
        }
        finally { Busy = false; }
        quit();
    }

    /// pack.ps1 -Bump в своём PowerShell, без окна. Ошибка — с последними
    /// строками сборки.
    static async Task Rebuild()
    {
        var script = Path.Combine(Source!, "pack.ps1").Replace("'", "''");
        var shell = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Source,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
            $"[Console]::OutputEncoding = [Text.Encoding]::UTF8; & '{script}' -Bump" })
            shell.ArgumentList.Add(argument);
        using var build = Process.Start(shell) ?? throw new InvalidOperationException("PowerShell didn't start");
        var tail = new Queue<string>();
        void Heard(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (tail)
            {
                tail.Enqueue(line.Trim());
                if (tail.Count > 3) tail.Dequeue();
            }
        }
        build.OutputDataReceived += (_, said) => Heard(said.Data);
        build.ErrorDataReceived += (_, said) => Heard(said.Data);
        build.BeginOutputReadLine();
        build.BeginErrorReadLine();
        await build.WaitForExitAsync();
        if (build.ExitCode != 0) lock (tail) throw new InvalidOperationException($"The build failed: {string.Join(" ", tail)}");
    }
}
