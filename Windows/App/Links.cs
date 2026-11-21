using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace Browser;

// Ссылки извне (Links.swift апстрима): адрес или страница на диске, с которыми
// запустили aerio.exe, открываются вкладкой. Окно на мир одно: второй запуск
// отдаёт свои адреса первому и выходит, иначе два окна делили бы вкладки,
// историю и папку движка. Адреса, пришедшие раньше окна, ждут его здесь.
// Браузером по умолчанию aerio себя не записывает: это решение владельца.
static class Links
{
    static readonly List<Uri> waiting = [];
    static Action<IReadOnlyList<Uri>>? deliver;
    static DispatcherQueue? ui;

    /// До окна, в Main. true — это второй запуск: адреса отданы первому, выходим.
    public static bool Elsewhere()
    {
        // Ключ — папка мира: тестовое окно не перехватывает запуски настоящего.
        var owner = AppInstance.FindOrRegisterForKey("aerio " + Store.Folder);
        if (owner.IsCurrent)
        {
            waiting.AddRange(Address.Launched(Environment.GetCommandLineArgs().Skip(1)));
            owner.Activated += (_, args) => Arrived(args);
            return false;
        }
        // Ждём, пока первый примет, не останавливая COM этого потока: так
        // советует документация Windows App SDK (простое Wait может зависнуть).
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        using var done = new ManualResetEvent(false);
        Task.Run(() =>
        {
            try { owner.RedirectActivationToAsync(args).AsTask().Wait(); }
            finally { done.Set(); }
        });
        _ = CoWaitForMultipleObjects(0, uint.MaxValue, 1, [done.SafeWaitHandle.DangerousGetHandle()], out _);
        // Окно первого — вперёд: право на это у только что запущенного, а не у
        // первого. Тихое окно вперёд не выходит.
        if (!App.Quiet)
        {
            try { SetForegroundWindow(Process.GetProcessById((int)owner.ProcessId).MainWindowHandle); }
            catch (ArgumentException) { }   // первый уже вышел
        }
        return true;
    }

    /// Окно готово: пришедшие до него адреса и все следующие — ему, в его потоке.
    public static void Serve(DispatcherQueue queue, Action<IReadOnlyList<Uri>> take)
    {
        List<Uri> early;
        lock (waiting)
        {
            ui = queue;
            deliver = take;
            early = [.. waiting];
            waiting.Clear();
        }
        if (early.Count > 0) take(early);
    }

    // Приходит не в потоке окна.
    static void Arrived(AppActivationArguments args)
    {
        var line = args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ? launch.Arguments : "";
        var urls = Address.Launched(Address.Words(line)).ToList();
        lock (waiting)
        {
            if (deliver is null) { waiting.AddRange(urls); return; }
            var take = deliver;
            ui?.TryEnqueue(() => take(urls));
        }
    }

    [DllImport("ole32.dll")]
    static extern int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr window);
}
