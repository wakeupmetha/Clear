namespace Browser;

// Перенос Sources/Search/Store.swift (Store и Disk).
//
// Где лежит всё, что браузер хранит. Одно место и одно правило: тестовый
// запуск никогда не трогает папку браузера, которым человек реально
// пользуется. В апстриме общая папка однажды стоила человеку закреплённых
// вкладок.
public static class Store
{
    /// Имя браузера: заголовок окна и папка хранилища. Строчными — так пишется
    /// бренд владельца. Имя Search и его иконка принадлежат Office Commun.
    public const string Name = "aerio";

    /// Тестовый запуск: либо он сам так сказал, либо запущен прямо из папки
    /// сборки, а не из установленного приложения. Вторая половина не
    /// перестраховка: просить человека помнить про флаг — это не защита.
    public static bool Testing =>
        Environment.GetEnvironmentVariable("BROWSER_PROBE") is not null
        || AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    /// Тестовый мир. BROWSER_PROBE=1 или запуск из папки сборки — «test»;
    /// BROWSER_PROBE=имя — отдельный мир, чтобы два прогона не делили данные.
    public static string? World
    {
        get
        {
            if (!Testing) return null;
            var asked = new string((Environment.GetEnvironmentVariable("BROWSER_PROBE") ?? "").ToLowerInvariant()
                .Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
            return asked is "" or "1" or "test" ? "test" : asked;
        }
    }

    public static string Folder { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        World is { } world ? $"{Name} ({world})" : Name);

    /// Для проверок: всё хранится в указанной папке.
    public static void Use(string folder) => Folder = folder;

    public static string File(string name) => Path.Combine(Folder, name);

    /// Файл, который не разобрался, откладывается в сторону, а не затирается
    /// следующим сохранением: закладки, историю и сессию никто не хочет терять
    /// из-за одного плохого чтения без следа того, что там было.
    public static void Quarantine(string file)
    {
        if (!System.IO.File.Exists(file)) return;
        var aside = Path.Combine(Path.GetDirectoryName(file) ?? "",
            $"{Path.GetFileNameWithoutExtension(file)}.unreadable-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.json");
        try { System.IO.File.Move(file, aside, overwrite: true); }
        // Не получилось — чтение всё равно уже вернуло пустоту, больше делать нечего.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// Запись файлов в фоне: по одной и так, что последней оказывается самая новая.
///
/// В апстриме каждое сохранение когда-то уходило в параллельную очередь, и
/// сессия, записанная при выходе, могла быть затёрта более старой, ещё
/// бывшей в пути: возвращались вчерашние вкладки. Здесь одна очередь, а
/// запись, которую обогнала более новая запись того же файла, отбрасывается.
public static class Disk
{
    static readonly Lock Gate = new();
    static readonly Dictionary<string, int> Newest = new(StringComparer.OrdinalIgnoreCase);
    static int count;
    static Task queue = Task.CompletedTask;

    /// encode выполняется там же, где запись. now пишет в вызывающем потоке:
    /// выход из приложения не ждёт очередь.
    public static void Write(string file, Func<byte[]?> encode, bool now = false)
    {
        var turn = Claim(file);
        void Put()
        {
            if (!Current(file, turn) || encode() is not { } data) return;
            // Пишем рядом без блокировки и только подмену делаем под ней:
            // иначе срочная запись ждала бы фоновую, например всю историю.
            var folder = Path.GetDirectoryName(file) ?? "";
            var aside = Path.Combine(folder, $".{Path.GetFileName(file)}.{turn}.writing");
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(aside, data);
                lock (Gate)
                {
                    // Подмена атомарна: при сбое остаётся старый файл целиком.
                    if (Newest[file] == turn) { File.Move(aside, file, overwrite: true); return; }
                }
                File.Delete(aside);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(aside); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
            }
        }
        if (now) Put();
        else lock (Gate) queue = queue.ContinueWith(_ => Put(), TaskScheduler.Default);
    }

    /// Удаление после всех уже запрошенных записей этого файла.
    public static void Remove(string file)
    {
        var turn = Claim(file);
        lock (Gate) queue = queue.ContinueWith(_ =>
        {
            lock (Gate)
            {
                if (Newest[file] != turn) return;
                try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }, TaskScheduler.Default);
    }

    /// Всё запрошенное к этому моменту записано. Вызывать при выходе.
    public static void Drain()
    {
        Task pending;
        lock (Gate) pending = queue;
        pending.Wait();
    }

    static int Claim(string file)
    {
        lock (Gate) return Newest[file] = ++count;
    }

    static bool Current(string file, int turn)
    {
        lock (Gate) return Newest[file] == turn;
    }
}
