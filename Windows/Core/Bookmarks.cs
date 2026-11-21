using System.Text.Json;
using System.Text.Json.Serialization;

namespace Browser;

/// Закладка или папка (Bookmark из Bookmarks.swift). Ключи в bookmarks.json
/// те же, что на Mac, поэтому файл оттуда читается как есть, с папками.
public sealed class Bookmark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    /// Нет у папки.
    public string? Url { get; set; }
    public List<Bookmark>? Children { get; set; }

    [JsonIgnore] public bool IsFolder => Url is null;

    /// Как её читает список: имя, а у безымянной — адрес, как его пишет поле.
    [JsonIgnore] public string Label => !IsFolder && Title.Trim().Length == 0
        ? (Uri.TryCreate(Url, UriKind.Absolute, out var url) ? Address.Pretty(url) : Url!)
        : Title;
}

/// Закладки: папки и сайты в маленьком файле (Bookmarks.swift).
// ponytail: папки читаются и показываются, но завести, переименовать и
// перенести закладку здесь нельзя — нет карточки закладки (BookmarkCard).
// Понадобится — Move и Rename по образцу апстрима.
public static class Bookmarks
{
    static List<Bookmark> roots = [];
    /// Из какого файла прочитано: папку хранилища могут сменить (Store.Use).
    static string? loaded;

    public static event Action? Changed;

    public static IReadOnlyList<Bookmark> Roots => Values();

    /// Страница — в конец списка, ни о чём не спрашивая: имя — заголовок
    /// страницы. Уже сохранённая не повторяется (null).
    public static Bookmark? Add(Uri url, string title)
    {
        if (Find(url) is not null) return null;
        var made = new Bookmark { Title = title.Trim().Length == 0 ? Address.Pretty(url) : title, Url = url.AbsoluteUri };
        Values().Add(made);
        Save();
        return made;
    }

    /// Сайт с этим адресом, где бы он ни лежал.
    public static Bookmark? Find(Uri url) => Sites(Values(), []).Select(site => site.Node).FirstOrDefault(node => node.Url == url.AbsoluteUri);

    public static void Remove(Guid id)
    {
        if (Prune(Values(), id)) Save();
    }

    static bool Prune(List<Bookmark> nodes, Guid id)
    {
        if (nodes.RemoveAll(node => node.Id == id) > 0) return true;
        return nodes.Any(node => node.Children is { } kids && Prune(kids, id));
    }

    /// Все сайты по порядку, папки раскрыты, с именами папок, где сайт лежит.
    public static List<(Bookmark Node, string[] Path)> Sites() => [.. Sites(Values(), [])];

    static IEnumerable<(Bookmark Node, string[] Path)> Sites(List<Bookmark> nodes, string[] path) =>
        nodes.SelectMany(node => node.IsFolder
            ? Sites(node.Children ?? [], [.. path, node.Title])
            : new[] { (node, path) });

    /// Сайты, в имени или адресе которых есть все слова, в любом порядке и
    /// регистре (правило matches апстрима; папки здесь не ищутся — их и
    /// не открыть). Пустой запрос — все сайты.
    public static List<(Bookmark Node, string[] Path)> Matches(string text)
    {
        var words = text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return [.. Sites().Where(site => words.All((site.Node.Title + " " + site.Node.Url).ToLowerInvariant().Contains))];
    }

    static List<Bookmark> Values()
    {
        var file = Store.File("bookmarks.json");
        if (loaded == file) return roots;
        loaded = file;
        roots = [];
        byte[] data;
        try { data = File.ReadAllBytes(file); }
        // Только «файла нет» значит «закладок нет»; см. Session.Read.
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return roots; }
        List<Bookmark>? list = null;
        try { list = JsonSerializer.Deserialize(data, Json.Default.ListBookmark); }
        catch (JsonException) { }
        if (list is null) Store.Quarantine(file);
        else roots = list;
        return roots;
    }

    static void Save()
    {
        // Копия: кодирование идёт в фоне, а список тем временем меняется.
        var snapshot = Copy(roots);
        Disk.Write(loaded!, () => JsonSerializer.SerializeToUtf8Bytes(snapshot, Json.Default.ListBookmark));
        Changed?.Invoke();
    }

    static List<Bookmark> Copy(List<Bookmark> nodes) =>
        [.. nodes.Select(node => new Bookmark { Id = node.Id, Title = node.Title, Url = node.Url, Children = node.Children is { } kids ? Copy(kids) : null })];
}
