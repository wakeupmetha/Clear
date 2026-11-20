using System.Globalization;

namespace Browser;

// Перенос Sources/Search/Address.swift.
//
// Набранное в поле либо становится адресом, либо нет. Если нет, вызывающий
// отправляет слова в поисковик (Engines.Url). Угадывать молча нельзя: «todo»
// и «hello world» не сайты.
//
// Отличие от macOS: System.Uri нормализует адрес (хост в нижний регистр, «/»
// после хоста). Что набрал человек, хранит Uri.OriginalString; канонический
// вид — Uri.AbsoluteUri.
public static class Address
{
    // Схемы, которые окно показывает само. mailto: и ссылки приложений — чужая
    // работа: лучше отказать, чем открыть пустую вкладку.
    // edge: и chrome: — служебные страницы движка: флаги, сведения о версии и
    // графике. Сюда попадает только набранное человеком: страница на такой
    // адрес перейти не может, это запрещает сам движок.
    static readonly HashSet<string> Ours = ["http", "https", "file", "about", "data", "edge", "chrome"];

    /// Путь к файлу на диске. На диск смотрит только подтверждённый переход:
    /// подсказки и перетащенный текст сюда не ходят. Только обычный файл: не
    /// папка, не ссылка, не устройство.
    public static Uri? File(string typed)
    {
        var text = typed.Trim();
        if (!Path.IsPathFullyQualified(text)) return null;
        try
        {
            const FileAttributes other = FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device;
            return (System.IO.File.GetAttributes(text) & other) == 0 ? new Uri(text) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static Uri? Url(string typed)
    {
        var text = typed.Trim();
        if (text.Length == 0 || text.Contains(' ')) return null;

        // Написано со схемой — верим на слово.
        var split = text.IndexOf("://", StringComparison.Ordinal);
        if (split >= 0)
        {
            var scheme = text[..split].ToLowerInvariant();
            // Своя страница — только из тех, что есть: остальное уйдёт в поиск.
            if (scheme == Own.Scheme) return Parse(text) is { } own && Own.Title(own) is not null ? own : null;
            return Ours.Contains(scheme) ? Parse(text) : null;
        }
        if (text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return Parse(text);

        // Всё остальное должно быть похоже на хост, прежде чем получит схему.
        var cut = text.AsSpan().IndexOfAny('/', '?', '#');
        var head = cut < 0 ? text : text[..cut];
        if (head.Contains('@')) return null;   // почтовый адрес
        if (head.StartsWith("[::1]", StringComparison.Ordinal)) return Parse("http://" + text);
        var host = head.Split(':', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? head;
        if (!LooksLikeHost(host)) return null;

        // У локального сервера почти никогда нет сертификата: https там — это
        // ошибка соединения, а не страница. То же для устройств в сети по
        // имени: printer.local, homeassistant.local.
        var local = host == "localhost"
            || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal)
            || host is "127.0.0.1" or "0.0.0.0"
            || PrivateRange(host);
        return Parse((local ? "http://" : "https://") + text);
    }

    static Uri? Parse(string text) => Uri.TryCreate(text, UriKind.Absolute, out var url) ? url : null;

    static bool Octet(string text, out byte value) =>
        byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    // Частные диапазоны, где живут Docker и офисные машины. Ровно четыре
    // числа: 10.io — сайт, а не 10.0.0.0/8.
    static bool PrivateRange(string host)
    {
        var parts = host.Split('.');
        if (parts.Length != 4) return false;
        Span<byte> o = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
            if (!Octet(parts[i], out o[i])) return false;
        return o[0] == 10 || (o[0] == 192 && o[1] == 168) || (o[0] == 172 && o[1] is >= 16 and <= 31);
    }

    /// Дев-серверы печатают 0.0.0.0 как адрес, который надо открыть. Это тот же
    /// сервер, что и localhost, поэтому открываем его. Null, если адрес не такой.
    public static Uri? Reachable(Uri url) =>
        url.Host == "0.0.0.0" && url.Scheme is "http" or "https"
            ? new UriBuilder(url) { Host = "localhost" }.Uri
            : null;

    static bool LooksLikeHost(string host)
    {
        if (host == "localhost") return true;

        var labels = host.Split('.');
        // Четыре числа — чаще всего адрес в локальной сети.
        if (labels.Length == 4 && labels.All(label => Octet(label, out _))) return true;
        if (labels.Length < 2) return false;
        foreach (var label in labels)
        {
            if (label.Length == 0 || label[0] == '-' || label[^1] == '-') return false;
            if (!label.All(c => char.IsLetterOrDigit(c) || c == '-')) return false;
        }

        // Решает последняя метка: с буквами на конце — домен, с цифрами —
        // номер версии.
        var tld = labels[^1];
        return tld.Length >= 2 && tld.All(char.IsLetter);
    }

    /// Адрес страницы для правки в поле: то, что при отправке как есть ведёт
    /// на ту же страницу. Схема убирается, только если поле само вернёт именно
    /// её; порт, запрос, фрагмент и обычный http наружу остаются.
    public static string Editable(Uri page)
    {
        var full = page.AbsoluteUri;
        var prefix = page.Scheme + "://";
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return full;
        var rest = full[prefix.Length..];
        List<string> candidates = [rest];
        if (page.AbsolutePath == "/" && page.Query.Length == 0 && page.Fragment.Length == 0 && rest.EndsWith('/'))
            candidates.Insert(0, rest[..^1]);
        foreach (var candidate in candidates)
            if (Url(candidate)?.AbsoluteUri == full) return candidate;
        return full;
    }

    /// Адрес, как его показывает строка списка: без www в начале.
    public static string WithoutWWW(string address) =>
        address.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? address[4..] : address;

    /// Что написано на вкладке, пока страница не сообщила заголовок.
    public static string Pretty(Uri url)
    {
        if (url.Host.Length == 0) return url.AbsoluteUri;
        var bare = WithoutWWW(url.Host);
        return url.AbsolutePath is "" or "/" ? bare : bare + url.AbsolutePath;
    }
}

/// Свои страницы браузера: настройки и история. Каждая живёт во вкладке
/// со своим адресом, как chrome://settings: её можно оставить открытой рядом с
/// работой, вернуться по Ctrl+Tab, закрыть по Ctrl+W. Перенос InternalPage из
/// Sources/Search/InternalPages.swift.
///
/// До движка такой адрес не доходит: вкладку рисует сама оболочка.
public static class Own
{
    /// В апстриме это search://. Здесь схема идёт за рабочим именем
    /// (Store.Name) и сменится вместе с ним.
    public const string Scheme = "browser";

    public static readonly Uri Settings = new(Scheme + "://settings");
    public static readonly Uri History = new(Scheme + "://history");

    public static bool Is(Uri url) => url.Scheme == Scheme;

    /// Название страницы на вкладке. Null — такой страницы нет.
    public static string? Title(Uri url) => !Is(url) ? null : url.Host switch
    {
        "settings" => "Settings",
        "history" => "History",
        _ => null,
    };
}
