using System.Globalization;
using System.Text.Json;

namespace Browser;

/// Скачанный файл в списке (Keep из Loot.swift апстрима): имя, откуда, где
/// лежит, когда. Ключи в downloads.json те же, что на Mac.
public sealed class Keep
{
    public required string Name { get; set; }
    /// Сайт, откуда файл: строка под именем.
    public required string From { get; set; }
    public required string Path { get; set; }
    public DateTime Date { get; set; }
    /// Адрес файла. Нет у файла из приватной вкладки и у адреса, который вне
    /// страницы ничего не значит (blob:, data:).
    public string? Address { get; set; }
}

/// Память о скачанном (Loot.swift): файл, скачанный час назад, — в один
/// щелчок от Проводника, а не поиск по папке. Сами загрузки ведёт движок;
/// здесь только список законченных, новые первыми.
public static class Downloads
{
    /// Пятьдесят — больше, чем кто-нибудь пролистывает назад (апстрим).
    public const int Most = 50;

    static List<Keep> kept = [];
    /// Из какого файла прочитано: папку хранилища могут сменить (Store.Use).
    static string? loaded;

    /// Список изменился.
    public static event Action? Changed;

    public static IReadOnlyList<Keep> Kept => Values();

    public static void Add(Keep keep)
    {
        var all = Values();
        all.RemoveAll(other => other.Path == keep.Path);
        all.Insert(0, keep);
        if (all.Count > Most) all.RemoveRange(Most, all.Count - Most);
        Save();
    }

    public static void Forget(string path)
    {
        if (Values().RemoveAll(keep => keep.Path == path) > 0) Save();
    }

    /// Только список. Файлы, которые человек просил, — его, и стирать их —
    /// дело Проводника, а не браузера (апстрим).
    public static void ForgetAll()
    {
        if (Values().Count == 0) return;
        Values().Clear();
        Save();
    }

    /// Адрес, по которому файл может взять и другая программа: только http и
    /// https, без имени и пароля (Keep.address апстрима). blob: и data: вне
    /// страницы ничего не значат, а data: — это ещё и весь файл.
    public static string? Address(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) return null;
        return url.UserInfo.Length == 0 ? url.AbsoluteUri : new UriBuilder(url) { UserName = "", Password = "" }.Uri.AbsoluteUri;
    }

    /// Размер, как его пишет macOS: в тысячах, с десятой долей до десяти.
    public static string Size(long bytes)
    {
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        var value = bytes / 1000.0;
        var unit = 0;
        while (value >= 999.5 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return value.ToString(value < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    static List<Keep> Values()
    {
        var file = Store.File("downloads.json");
        if (loaded == file) return kept;
        loaded = file;
        kept = [];
        byte[] data;
        try { data = File.ReadAllBytes(file); }
        // Только «файла нет» значит «списка нет»; см. Session.Read.
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return kept; }
        List<Keep>? list = null;
        try { list = JsonSerializer.Deserialize(data, Json.Default.ListKeep); }
        catch (JsonException) { }
        if (list is null) Store.Quarantine(file);
        else kept = [.. list.Where(keep => keep is { Name: not null, Path: not null, From: not null }).Take(Most)];
        return kept;
    }

    static void Save()
    {
        // Копия: кодирование идёт в фоне, а список тем временем меняется.
        var snapshot = kept.ToList();
        Disk.Write(loaded!, () => JsonSerializer.SerializeToUtf8Bytes(snapshot, Json.Default.ListKeep));
        Changed?.Invoke();
    }
}
