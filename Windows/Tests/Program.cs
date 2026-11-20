using Browser;
using Browser.Tests;

// Проверки ядра. Без фреймворка: каждая проверка — условие и фраза о том, что
// должно быть верно; код выхода 1, если хоть одна не прошла. Наборы для
// адресов и истории перенесены из Tests/AddressRegression и
// Tests/HistoryRegression апстрима: это спецификация поведения, которое
// Windows-версия обязана сохранить.
//
// Всё пишется во временную папку и удаляется в конце; настоящих данных
// браузера проверки не касаются.

Check.Root = Path.Combine(Path.GetTempPath(), "browser-core-" + Guid.NewGuid().ToString("N"));
// Чтобы не ждать полторы секунды на каждую запись истории.
History.SavePause = TimeSpan.FromMilliseconds(20);
try
{
    AddressTests.Run();
    StoreTests.Run();
    await StoreTests.BenchAnswers();
    HistoryTests.Run();
    await IconsTests.Run();
    PageKeysTests.Run();
    ExtensionsTests.Run();
    CrxTests.Run();
}
finally
{
    // Таймеры историй могут ещё дописывать свои файлы.
    Thread.Sleep(200);
    Disk.Drain();
    try { Directory.Delete(Check.Root, recursive: true); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
}

if (Check.Failures > 0)
{
    Console.Error.WriteLine($"Не прошло проверок: {Check.Failures} из {Check.Failures + Check.Passed}.");
    return 1;
}
Console.WriteLine($"Все проверки прошли: {Check.Passed}.");
return 0;

static class Check
{
    public static int Failures, Passed;
    public static string Root = "";

    public static void Expect(bool ok, string what)
    {
        if (ok) { Passed++; return; }
        Failures++;
        Console.Error.WriteLine("FAIL: " + what);
    }

    /// Свежее хранилище под именем: как Store.use в заглушках апстрима.
    public static void World(string name) => Store.Use(Path.Combine(Root, name));
}
