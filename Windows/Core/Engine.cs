namespace Browser;

// Перенос Sources/Search/Engine.swift: поисковики и адрес запроса по шаблону.
public enum Engine { Google, DuckDuckGo, Bing, Ecosia, Startpage, Kagi, Brave, Qwant, Custom }

public static class Engines
{
    public const Engine Standard = Engine.Google;

    /// Поисковик из настроек. Ключи и значения те же, что в апстриме:
    /// «search.engine» хранит имя строчными («duckduckgo»).
    public static Engine Chosen =>
        Enum.TryParse<Engine>(Settings.Text("search.engine"), ignoreCase: true, out var engine) && Enum.IsDefined(engine)
            ? engine
            : Standard;

    /// Как поисковик записан в настройках.
    public static string Key(this Engine engine) => engine.ToString().ToLowerInvariant();

    /// Шаблон, по которому сейчас уходят слова из поля.
    public static string Current => Chosen.Template(Settings.Text("search.custom") ?? "");

    public static string Title(this Engine engine) => engine switch
    {
        Engine.Brave => "Brave Search",
        _ => engine.ToString(),
    };

    public static string Template(this Engine engine, string custom = "") => engine switch
    {
        Engine.Google => "https://www.google.com/search?q=%s",
        Engine.DuckDuckGo => "https://duckduckgo.com/?q=%s",
        Engine.Bing => "https://www.bing.com/search?q=%s",
        Engine.Ecosia => "https://www.ecosia.org/search?q=%s",
        Engine.Startpage => "https://www.startpage.com/sp/search?query=%s",
        Engine.Kagi => "https://kagi.com/search?q=%s",
        Engine.Brave => "https://search.brave.com/search?q=%s",
        Engine.Qwant => "https://www.qwant.com/?q=%s",
        // Негодный свой шаблон не должен молча ломать поиск: уходим в стандартный.
        _ => Accepts(Written(custom)) ? Written(custom) : Standard.Template(),
    };

    public static string Name(this Engine engine, string custom = "") =>
        engine != Engine.Custom ? engine.Title() : BareHost(Written(custom)) ?? Standard.Title();

    /// Свой шаблон, как его приносят люди: скопированный из другого браузера с
    /// {searchTerms} или набранный без https://. Принимаем оба вида.
    public static string Written(string template)
    {
        var text = template.Trim().Replace("{searchTerms}", "%s");
        return text.Length > 0 && !text.Contains("://") ? "https://" + text : text;
    }

    public static bool Accepts(string template) => Host(template) is not null;

    /// Хост шаблона без www: так сайт назвал бы человек.
    public static string? BareHost(string template) => Host(template) is { } host ? Address.WithoutWWW(host) : null;

    public static Uri? Url(string text, string template)
    {
        var words = text.Trim();
        if (words.Length == 0) return null;
        // Сначала проверяем сам шаблон с меткой на месте слов, потом ставим
        // слова: так их экранирование не зависит от разбора шаблона.
        if (!Uri.TryCreate(template.Replace("%s", Mark), UriKind.Absolute, out var shape)) return null;
        var address = shape.AbsoluteUri.Replace(Mark, Uri.EscapeDataString(words));
        return Uri.TryCreate(address, UriKind.Absolute, out var url) ? url : null;
    }

    const string Mark = "SEARCHWORDSGOHERE";

    // Хост не должен зависеть от слов запроса: иначе шаблон отправлял бы
    // набранное на произвольный сайт.
    static string? Host(string template)
    {
        var trimmed = template.Trim();
        if (!trimmed.Contains("%s")) return null;
        if (!Uri.TryCreate(trimmed.Replace("%s", "a"), UriKind.Absolute, out var one)
            || !Uri.TryCreate(trimmed.Replace("%s", "b"), UriKind.Absolute, out var other)) return null;
        if (one.Scheme is not ("http" or "https") || one.Host.Length == 0 || one.Host != other.Host) return null;
        return one.Host;
    }
}
