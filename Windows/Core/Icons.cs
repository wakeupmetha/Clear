using System.Security.Cryptography;
using System.Text;

namespace Browser;

// Перенос Sources/Search/Icons.swift (значки сайтов), упрощённый.
//
// Значок нужен вкладкам и истории, но открывать страницу без него можно: поэтому
// всё здесь, что сломалось, тихо даёт «значка нет», а не ошибку.
//
// Что не перенесено и почему:
// - значок на страницу и тёмный вариант: держим один значок на сайт;
// - значок, объявленный страницей (link rel=icon), не ищем здесь: его отдаст
//   движок, когда страницу откроют.
public static class Icons
{
    /// Сколько значков держим на диске. Больше — удаляем самые старые по записи.
    public static int Most = 4000;

    const int MaxBytes = 1_048_576;

    static readonly Lock Gate = new();
    /// Сайты, которых уже спрашивали в эту сессию. Вторая просьба получает ту же
    /// задачу, даже если первая не нашла значка: иначе сайт без значка
    /// опрашивался бы при каждом открытии своей страницы.
    static readonly Dictionary<string, Task<bool>> Asked = new();
    /// Сколько раз значки забывали. Запрос, начатый до Clear History, после него
    /// значок не кладёт: иначе стёртое возвращалось бы на диск.
    static int era;
    static readonly SemaphoreSlim Slots = new(4);
    static readonly HttpClient Client = CreateClient();

    public static string Folder => Store.File("icons");

    /// Сайт адреса: хост строчными без «www.», порт — только если он не по умолчанию.
    /// Null для всего, что не сайт (браузерные страницы, файлы с диска).
    public static string? Site(Uri url)
    {
        if (url.Scheme is not ("http" or "https")) return null;
        var host = url.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        if (host.Length == 0) return null;
        return url.IsDefaultPort ? host : $"{host}:{url.Port}";
    }

    /// Имя файла по сайту. Хэш, а не сам сайт: имя из адреса может оказаться
    /// чем угодно, а здесь нужно только одно имя на сайт.
    public static string File(string site)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(site));
        return Path.Combine(Folder, Convert.ToHexString(hash)[..32].ToLowerInvariant() + ".icon");
    }

    public static string? Find(Uri url)
    {
        if (Site(url) is not { } site) return null;
        var path = File(site);
        return System.IO.File.Exists(path) ? path : null;
    }

    /// Годится ли тело как значок. Проверяем заголовок по списку форматов и размер.
    /// Почему так: каждый лишний формат — ещё один декодер, до которого дотягивается
    /// чужой сайт. HTML, SVG и всё прочее отсекаем.
    public static bool Usable(byte[] data)
    {
        if (data.Length < 60 || data.Length > MaxBytes) return false;
        return Starts(data, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
            || Starts(data, [0x00, 0x00, 0x01, 0x00])
            || Starts(data, [0xFF, 0xD8, 0xFF])
            || Starts(data, "GIF8"u8)
            || Starts(data, "BM"u8)
            || (Starts(data, "RIFF"u8) && data.AsSpan(8).StartsWith("WEBP"u8));
    }

    public static bool Keep(Uri url, byte[] data)
    {
        if (Site(url) is not { } site || !Usable(data)) return false;
        // Сразу, а не через очередь: окно читает значок тотчас после Keep, и очередь
        // записи не должна его опережать.
        Disk.Write(File(site), () => data, now: true);
        Prune();
        return true;
    }

    public static void Prune()
    {
        try
        {
            var folder = new DirectoryInfo(Folder);
            if (!folder.Exists) return;
            var files = folder.GetFiles("*.icon");
            if (files.Length <= Most) return;
            // Режем до трёх четвертей, а не до предела: иначе каждый новый значок
            // удалял бы один файл и каждое сохранение трогало папку.
            foreach (var old in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - Most * 3 / 4))
                old.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// Clear History: значки тоже говорят, где человек был. Забываем и то, кого уже
    /// спрашивали, чтобы сайты снова получили шанс.
    public static void ForgetAll()
    {
        // Сначала дописать уже поставленное: иначе запись обогнала бы удаление
        // и вернула бы значок на диск.
        Disk.Drain();
        lock (Gate)
        {
            era++;
            try { Directory.Delete(Folder, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Asked.Clear();
        }
    }

    /// Значок сайта есть на диске после ожидания. Сеть — только для сайта, которого
    /// ещё нет на диске и о котором не спрашивали в эту сессию.
    // ponytail: для сайта без значка берём только /favicon.ico. Значок, объявленный
    // страницей, придёт от движка при открытии; добавлять разбор HTML, когда это
    // станет видно человеку.
    public static Task<bool> Ensure(Uri page)
    {
        if (Site(page) is not { } site) return Task.FromResult(false);
        if (Find(page) is not null) return Task.FromResult(true);
        lock (Gate)
        {
            if (!Asked.TryGetValue(site, out var asking)) Asked[site] = asking = Fetch(page);
            return asking;
        }
    }

    static async Task<bool> Fetch(Uri page)
    {
        // Ensure зовёт Fetch под Gate: эпоха читается до первого ожидания.
        var started = era;
        await Slots.WaitAsync();
        try
        {
            using var response = await Client.GetAsync(
                page.GetLeftPart(UriPartial.Authority) + "/favicon.ico", HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return false;
            if (response.Content.Headers.ContentLength > MaxBytes) return false;
            var data = await ReadCapped(response.Content);
            // Пока шли за /favicon.ico, движок мог отдать значок, объявленный
            // страницей: он точнее, его не перезаписываем.
            lock (Gate) return started == era && (Find(page) is not null || Keep(page, data));
        }
        catch (Exception)
        {
            // Любая беда с сетью, таймаутом или телом: значка нет, открытие страницы
            // от этого не страдает. Исключение, ушедшее из задачи, никто не ждёт.
            return false;
        }
        finally { Slots.Release(); }
    }

    /// Тело не больше предела плюс один байт: лишний байт говорит Usable, что тело
    /// слишком большое, и без чтения всего файла.
    static async Task<byte[]> ReadCapped(HttpContent content)
    {
        var buffer = new byte[MaxBytes + 1];
        using var stream = await content.ReadAsStreamAsync();
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled));
            if (read == 0) break;
            filled += read;
        }
        return buffer[..filled];
    }

    static HttpClient CreateClient()
    {
        // Запрос идёт от имени человека к сайту из его истории, поэтому без кук и входов.
        // Владелец разрешил ходить за значками сайтов, где значка ещё нет (решение 2026-11-20).
        var client = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        }) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Store.Name);
        return client;
    }

    static bool Starts(byte[] data, ReadOnlySpan<byte> magic) => data.AsSpan().StartsWith(magic);
}
