using System.Text;

namespace Browser.Tests;

static class AddressTests
{
    static void Becomes(string typed, string? wanted)
    {
        var got = Address.Url(typed)?.OriginalString;
        Check.Expect(got == wanted, $"«{typed}» стало {got ?? "ничем"}, ожидалось {wanted ?? "ничего"}");
    }

    public static void Run()
    {
        // Tests/AddressRegression/main.swift: какую схему получает набранный адрес.
        // Сервер на этой машине или в локальной сети: обычный http.
        Becomes("localhost:3000", "http://localhost:3000");
        Becomes("printer.local", "http://printer.local");
        Becomes("127.0.0.1:8080", "http://127.0.0.1:8080");
        Becomes("0.0.0.0:8080", "http://0.0.0.0:8080");
        Becomes("10.0.0.1", "http://10.0.0.1");
        Becomes("10.255.255.254:9000", "http://10.255.255.254:9000");
        Becomes("192.168.1.1", "http://192.168.1.1");
        Becomes("172.16.0.1", "http://172.16.0.1");
        Becomes("172.31.255.254", "http://172.31.255.254");
        // Сайт, как бы ни выглядело имя: https.
        Becomes("example.com", "https://example.com");
        Becomes("172.15.0.1", "https://172.15.0.1");
        Becomes("172.32.0.1", "https://172.32.0.1");
        Becomes("10.io", "https://10.io");
        Becomes("10.org/about", "https://10.org/about");
        Becomes("10.0.0.1.example.com", "https://10.0.0.1.example.com");
        Becomes("192.168.1.1.example.com", "https://192.168.1.1.example.com");
        // Вообще не место.
        Becomes("hello world", null);
        Becomes("todo", null);

        // Tests/SearchTests/AddressTests.swift.
        Becomes("localhost:3000/test", "http://localhost:3000/test");
        Becomes("https://example.com/a?q=b#c", "https://example.com/a?q=b#c");
        Becomes("[::1]:3000", "http://[::1]:3000");
        // Служебные страницы движка набираются как есть.
        Becomes("edge://flags", "edge://flags");
        Becomes("chrome://gpu", "chrome://gpu");
        Check.Expect(Address.Editable(new Uri("edge://flags")) == "edge://flags/", "служебный адрес в поле остаётся со схемой");
        // Свои страницы: только те, что есть.
        Becomes("browser://settings", "browser://settings");
        Becomes("browser://nothing", null);
        Check.Expect(Address.Url("BROWSER://settings/") == Own.Settings, "адрес своей страницы один, как его ни набери");
        Check.Expect(Own.Is(Own.Settings) && Own.Title(Own.Settings) == "Settings" && !Own.Is(new Uri("https://settings/")), "своя страница узнаётся по схеме");
        Check.Expect(Address.Editable(Own.Settings) == "browser://settings/", "адрес своей страницы в поле остаётся со схемой");
        Check.Expect(Address.Url("browser://history") == Own.History && Own.Title(Own.History) == "History", "история — тоже своя страница");
        foreach (var text in (string[])["", "two words", "./report.pdf", "person@example.com", "javascript:alert(1)", "mailto:a@b.c"])
        {
            Becomes(text, null);
            Check.Expect(Address.File(text) is null, $"«{text}» не путь к файлу");
        }

        Files();

        // Адрес для правки возвращается на ту же страницу.
        Check.Expect(Address.Editable(new Uri("https://example.com/")) == "example.com", "https и пустой путь убираются");
        Check.Expect(Address.Editable(new Uri("https://example.com/a?q=1")) == "example.com/a?q=1", "путь и запрос остаются");
        Check.Expect(Address.Editable(new Uri("http://example.com/")) == "http://example.com/", "обычный http наружу остаётся целиком");
        Check.Expect(Address.Editable(new Uri("http://localhost:3000/")) == "localhost:3000", "http на этой машине убирается: поле вернёт его само");
        Check.Expect(Address.Pretty(new Uri("https://www.example.com/")) == "example.com", "вкладка без www и без пустого пути");
        Check.Expect(Address.Pretty(new Uri("https://www.example.com/a/b")) == "example.com/a/b", "вкладка с путём");
        Check.Expect(Address.Reachable(new Uri("http://0.0.0.0:8080/x"))?.AbsoluteUri == "http://localhost:8080/x", "0.0.0.0 открывается как localhost");
        Check.Expect(Address.Reachable(new Uri("http://example.com/")) is null, "обычный адрес не переписывается");

        Engines();
        Slips();
    }

    static void Files()
    {
        Check.World("files");
        Directory.CreateDirectory(Store.Folder);
        foreach (var name in (string[])["report.pdf", "résumé #1 100%.html"])
        {
            var path = Store.File(name);
            File.WriteAllText(path, "local file");
            var url = Address.File($" {path}\n");
            Check.Expect(url is { IsFile: true } && url.LocalPath == path, $"путь к файлу «{name}» сохраняет имя буквально");
            Check.Expect(Address.Url(path) is null, "подсказки и перетащенный текст пути не разрешают");
            Check.Expect(url is not null && Address.Url(url.AbsoluteUri) == url, "адрес file:// возвращается к тому же файлу");
        }
        Check.Expect(Address.File(Store.Folder) is null, "папка не файл");
        Check.Expect(Address.File(Store.File("missing.pdf")) is null, "отсутствующий файл не файл");
    }

    static void Engines()
    {
        var google = Engine.Google.Template();
        Check.Expect(Browser.Engines.Url("hello world", google)?.AbsoluteUri == "https://www.google.com/search?q=hello%20world", "слова уходят в поисковик");
        Check.Expect(Browser.Engines.Url("c++ & c#", google)?.AbsoluteUri == "https://www.google.com/search?q=c%2B%2B%20%26%20c%23", "знаки в словах не ломают запрос");
        Check.Expect(Browser.Engines.Url("   ", google) is null, "пустой запрос никуда не ведёт");
        Check.Expect(Browser.Engines.Written("duckduckgo.com/?q={searchTerms}") == "https://duckduckgo.com/?q=%s", "шаблон из другого браузера принимается");
        Check.Expect(Engine.Custom.Template("search.example/?q={searchTerms}") == "https://search.example/?q=%s", "свой поисковик без https://");
        Check.Expect(Engine.Custom.Name("https://www.search.example/?q=%s") == "search.example", "свой поисковик называется по хосту");
        Check.Expect(Engine.Custom.Template("no placeholder") == google, "негодный шаблон уходит в стандартный поисковик");
        Check.Expect(!Browser.Engines.Accepts("https://%s.example/"), "слова запроса не могут выбирать хост");
        Check.Expect(Engine.Brave.Title() == "Brave Search" && Engine.DuckDuckGo.Title() == "DuckDuckGo", "названия поисковиков");
    }

    static void Slips()
    {
        static byte[] B(string text) => Encoding.UTF8.GetBytes(text);
        var page = B("swift guide docs.example-test.org/guide/concurrency");
        Check.Expect(Fuzzy.Match([B("concurrency")], page) == 1, "слово как набрано");
        Check.Expect(Fuzzy.Match([B("concurency")], page) == 0, "пропущенная буква прощается");
        Check.Expect(Fuzzy.Match([B("guide"), B("kotlin")], page) is null, "слова, которого нет, нет");
        Check.Expect(Fuzzy.Nearly(B("gihtub"), B("github github.com")), "переставленные буквы — одна опечатка");
        Check.Expect(!Fuzzy.Nearly(B("gth"), B("github github.com")), "короче четырёх букв не угадываем");
        Check.Expect(!Fuzzy.Nearly(B("xithub"), B("github github.com")), "первая буква должна совпасть");
        Check.Expect(Fuzzy.StartsWord(B("api"), B("team/api")), "«api» начинает слово в «team/api»");
        Check.Expect(!Fuzzy.StartsWord(B("api"), B("rapid")), "но не в «rapid»");
    }
}
