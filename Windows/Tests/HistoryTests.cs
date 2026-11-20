namespace Browser.Tests;

// Перенос Tests/HistoryRegression/main.swift. Имена миров и адреса те же, чтобы
// сверять с апстримом построчно. Не перенесён только перенос «кредитов» из
// файлов до 1.0.4: самой этой миграции в ядре нет.
static class HistoryTests
{
    // 1 700 000 000 секунд Unix в датах Swift (секунды от 2001 года).
    const long Then = 721_692_800;

    static Uri U(string address) => new(address);

    static List<History.Trace> Traces(History history, string host) =>
        history.Everything().Where(trace => trace.Url.Host == host).ToList();

    static string Kinds(IEnumerable<Suggestion> options) => string.Join(", ", options.Select(option => option.Key));

    static void Seed(string json)
    {
        Directory.CreateDirectory(Store.Folder);
        File.WriteAllText(Store.File("history.json"), json);
    }

    /// Даёт отложенной записи дойти до диска и читает, что получилось.
    static string Saved()
    {
        Thread.Sleep(150);
        Disk.Drain();
        var file = Store.File("history.json");
        return File.Exists(file) ? File.ReadAllText(file) : "";
    }

    public static void Run()
    {
        Identities();
        OneLetter();
        RetitleAndForget();
        LegacyLoad();
        RootCredits();
        SuggestionsAndCompletion();
        Credentials();
        WwwCompletion();
        TitlesPathsAndSlips();
        TitleChurn();
        Capped();
        Days();
    }

    /// Страница истории: дни по местному времени, под каждым его места.
    static void Days()
    {
        var noon = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Local);
        History.Trace At(DateTime when) => new("key", "days.example", "Days", U("https://days.example/"), when.ToUniversalTime(), 1);
        // Полночь делит первые три: 12:00 и 00:30 сегодня, 23:30 вчера.
        var lines = History.Lines(
            [At(noon), At(noon.AddHours(-11.5)), At(noon.AddHours(-12.5)), At(noon.AddDays(-30)), At(noon.AddYears(-1))], noon);
        var drawn = string.Join(" ", lines.Select(line => line.Day ?? (line.First ? "[" : "") + "•" + (line.Last ? "]" : "")));
        Check.Expect(drawn == "Today [• •] Yesterday [•] 21 October [•] 20 November 2025 [•]", $"история разложена по дням: {drawn}");
        Check.Expect(History.Lines([], noon).Count == 0, "пустая история — пустой список");

        // Shift+щелчок: от прошлого выбранного до этого, через имя дня, в любую сторону.
        History.Trace Keyed(string key, DateTime when) => new(key, "days.example", "Days", U("https://days.example/" + key), when.ToUniversalTime(), 1);
        var keyed = History.Lines([Keyed("a", noon), Keyed("b", noon.AddHours(-1)), Keyed("c", noon.AddDays(-1)), Keyed("d", noon.AddDays(-1).AddHours(-1))], noon);
        Check.Expect(string.Join("", History.Between(keyed, "b", "d")) == "bcd", "диапазон вниз через имя дня — b c d");
        Check.Expect(string.Join("", History.Between(keyed, "c", "a")) == "abc", "диапазон вверх — a b c, в порядке списка");
        Check.Expect(string.Join("", History.Between(keyed, "gone", "c")) == "c" && History.Between(keyed, "a", "gone").Count == 0,
            "без прошлого выбранного — одно место; без места — ничего");
    }

    static void Identities()
    {
        Check.World("identity");
        var history = new History();
        string[] addresses =
        [
            "http://identity.example.test:8080/A?Q=One#first",
            "https://identity.example.test:8080/A?Q=One",
            "https://identity.example.test:9090/A?Q=One",
            "https://identity.example.test:8080/a?Q=One",
            "https://identity.example.test:8080/A?q=One",
            "https://identity.example.test:8080/A?Q=one",
        ];
        history.Record(U(addresses[0]), "HTTP");
        for (var i = 1; i < addresses.Length; i++)
            history.Take(U(addresses[i]), $"Imported {i - 1}", i + 1, DateTime.UnixEpoch.AddSeconds(1_700_000_000 + i - 1));
        // Фрагмент — место на странице, а не отдельная запись.
        history.Take(U("http://identity.example.test:8080/A?Q=One#later"), "", 8, DateTime.UnixEpoch.AddSeconds(1_700_000_100));

        var pages = Traces(history, "identity.example.test").Where(trace => trace.Url.AbsolutePath is "/A" or "/a").ToList();
        Check.Expect(pages.Count == 6, "схема, порт, регистр пути и регистр запроса различаются");
        Check.Expect(pages.Select(trace => trace.Key).Distinct().Count() == 6, "у каждой страницы своя идентичность");
        var original = pages.FirstOrDefault(trace => trace.Url is { Scheme: "http", Port: 8080 });
        Check.Expect(original?.Count == 8, "повтор, отличающийся только фрагментом, обновляет исходный визит");
        Check.Expect(pages.All(trace => !trace.Key.Contains('#')), "в идентичности нет фрагмента");

        Check.World("recorded-local-paths");
        var recorded = new History();
        foreach (var address in (string[])["http://localhost:3000/a", "http://localhost:4000/a", "http://localhost:3000/A", "http://localhost:4000/A"])
            recorded.Record(U(address), address);
        var local = Traces(recorded, "localhost").Where(trace => trace.Url.AbsolutePath != "/").ToList();
        Check.Expect(local.Count == 4 && local.Select(trace => trace.Key).Distinct().Count() == 4, "порт localhost и /A против /a различаются");
    }

    static void RetitleAndForget()
    {
        Check.World("retitle-forget");
        var history = new History();
        var insecure = U("http://titles.example.test:8080/A?Q=One");
        var secure = U("https://titles.example.test:8080/A?Q=One");
        history.Record(insecure, "Insecure title");
        history.Record(secure, "Secure title");
        history.Retitle(insecure, "Retitled HTTP");

        var pages = Traces(history, "titles.example.test").Where(trace => trace.Url.AbsolutePath == "/A").ToList();
        var http = pages.FirstOrDefault(trace => trace.Url.Scheme == "http");
        Check.Expect(pages.Count == 2, "разные схемы переименовываются отдельно");
        Check.Expect(http?.Title == "Retitled HTTP", "заголовок меняется только у своей схемы");
        Check.Expect(pages.FirstOrDefault(trace => trace.Url.Scheme == "https")?.Title == "Secure title", "у другой схемы заголовок прежний");

        if (http is not null) history.Forget(http.Key);
        pages = Traces(history, "titles.example.test").Where(trace => trace.Url.AbsolutePath == "/A").ToList();
        Check.Expect(pages is [{ Url.Scheme: "https" }], "забывается только выбранная страница");
    }

    static void LegacyLoad()
    {
        Check.World("legacy-load");
        Seed($$"""
            [{"url":"https://legacy.example.test:8080/A?Q=One#first","key":"stale-collision","title":"Upper","count":2,"last":{{Then}}},
             {"url":"https://legacy.example.test:8080/a?Q=One","key":"stale-collision","title":"Lower","count":3,"last":{{Then + 1}}},
             {"url":"https://legacy.example.test:8080/A?Q=One#again","key":"old-third-key","title":"Duplicate fragment","count":7,"last":{{Then + 2}}}]
            """);
        var history = new History();
        var pages = Traces(history, "legacy.example.test").Where(trace => trace.Url.AbsolutePath is "/A" or "/a").ToList();
        var upper = pages.FirstOrDefault(trace => trace.Url.AbsolutePath == "/A");
        Check.Expect(pages.Count == 2, "ключи из файла строятся заново, и регистр пути переживает загрузку");
        Check.Expect(upper?.Count == 9, "записи одной страницы складывают визиты");
        Check.Expect(upper?.Last == DateTime.UnixEpoch.AddSeconds(1_700_000_002), "дата из файла с Mac читается верно");
        Check.Expect(pages.FirstOrDefault(trace => trace.Url.AbsolutePath == "/a")?.Count == 3, "вариант с другим регистром пути хранит свой счёт");
        Check.Expect(pages.All(trace => !trace.Key.StartsWith("stale-") && trace.Key != "old-third-key"), "идентичность берётся из адреса, а не из старого ключа");
    }

    static void RootCredits()
    {
        Check.World("root-origins");
        var history = new History();
        foreach (var address in (string[])["http://origin.example.test:8080/page", "https://origin.example.test:8080/page", "https://origin.example.test:9090/page"])
            history.Record(U(address), "Page");
        var roots = history.Suggestions("ORIGIN.EXAMPLE.TEST", 50)
            .Where(option => option.Url is { Host: "origin.example.test", AbsolutePath: "/" });
        var origins = roots.Select(option => $"{option.Url.Scheme}:{option.Url.Port}").ToHashSet();
        Check.Expect(origins.SetEquals(["http:8080", "https:8080", "https:9090"]), "главная страница хранит схему и порт визита");
        Check.Expect(Traces(history, "origin.example.test").Count == 3, "служебные записи главной не добавляют строк в историю");

        Check.World("direct-root");
        var direct = new History();
        direct.Record(U("https://home.example.test/"), "Home");
        var home = Traces(direct, "home.example.test").Where(trace => trace.Url.AbsolutePath == "/").ToList();
        Check.Expect(home is [{ Count: 1 }], "визит на главную засчитывается ей один раз");
        direct.Record(U("https://home.example.test/deep"), "Deep page");
        home = Traces(direct, "home.example.test").Where(trace => trace.Url.AbsolutePath == "/").ToList();
        Check.Expect(home is [{ Count: 2, Title: "Home" }], "глубокая страница добавляет визит главной и не трогает её заголовок");

        Check.World("canonical-root");
        var canonical = new History();
        canonical.Record(U("HTTPS://Normalize.example.test"), "First form");
        canonical.Record(U("https://normalize.example.test/"), "Slash form");
        var normalized = Traces(canonical, "normalize.example.test").Where(trace => trace.Url.AbsolutePath == "/").ToList();
        Check.Expect(normalized is [{ Count: 2, Key: "https://normalize.example.test/" }], "регистр схемы и хоста и пустой путь дают одну главную");
    }

    static void SuggestionsAndCompletion()
    {
        Check.World("suggestions");
        var history = new History();
        history.Record(U("https://suggest.example.test/A?Q=One"), "Upper page");
        history.Record(U("https://suggest.example.test/a?Q=One"), "Lower page");
        history.Record(U("https://suggest.example.test/A?q=One"), "Query case");

        var options = history.Suggestions("SUGGEST.EXAMPLE.TEST", 50);
        Check.Expect(options.Any(option => option.Url.AbsolutePath == "/A"), "регистр хоста при поиске не важен");
        Check.Expect(options.Select(option => option.Id).Distinct().Count() == options.Count, "у подсказок разные id при разном регистре адреса");

        var upper = options.FirstOrDefault(option => option.Url is { AbsolutePath: "/A", Query: "?Q=One" });
        Check.Expect(upper is not null, "подсказка с путём в верхнем регистре есть");
        if (upper is not null)
        {
            Check.Expect(History.Completion("SUGGEST.EXAMPLE.TEST", [upper]) == "/A?Q=One", "дописывание не смотрит на регистр хоста");
            Check.Expect(History.Completion("suggest.example.test/a?Q=", [upper]) is null, "дописывание не меняет регистр пути");
            Check.Expect(History.Completion("suggest/A", [upper]) is null, "остаток хоста не приписывается после набранного пути");
        }

        Check.World("scheme-suggestion-ids");
        var schemes = new History();
        schemes.Record(U("http://scheme-id.example.test:3000/a?Q=One"), "HTTP");
        schemes.Record(U("https://scheme-id.example.test:3000/a?Q=One"), "HTTPS");
        var both = schemes.Suggestions("scheme-id.example.test", 50)
            .Where(option => option.Url is { Host: "scheme-id.example.test", AbsolutePath: "/a", Query: "?Q=One" }).ToList();
        Check.Expect(both.Select(option => option.Url.Scheme).Distinct().Count() == 2, "варианты, разные только схемой, оба остаются");
        Check.Expect(both.Select(option => option.Id).Distinct().Count() == both.Count, "и у них разные id");
        foreach (var candidate in both.Where(option => option.Key.Length > 2))
        {
            var typed = candidate.Key[..^1];
            var ending = History.Completion(typed, [candidate]);
            Check.Expect(ending is not null, "начало подсказки со своей схемой дописывается");
            var completed = ending is null ? null : Address.Url(typed + ending);
            Check.Expect(completed is not null && History.Identity(completed) == History.Identity(candidate.Url), "и ведёт на свой же адрес");
        }

        Check.World("known-suppression");
        var known = new History();
        known.Record(U("https://github.com/"), "GitHub home");
        Check.Expect(known.Suggestions("github", 50).All(option => option.Kind != Suggestion.Kinds.Known), "посещённый известный сайт не предлагается второй раз как известный");
    }

    static void Credentials()
    {
        Check.World("credentials");
        var history = new History();
        history.Record(U("https://user:secret@cred.example.test/page"), "Signed in");
        history.Take(U("https://user:secret@cred.example.test/other"), "Taken", 2, DateTime.UtcNow);
        history.Retitle(U("https://user:secret@cred.example.test/page"), "Retitled");
        history.Settle();
        var pages = Traces(history, "cred.example.test");
        Check.Expect(pages.Count == 2, "страница с именем и паролем в адресе всё равно хранится");
        Check.Expect(pages.FirstOrDefault(trace => trace.Url.AbsolutePath == "/page")?.Title == "Retitled", "заголовок находит страницу, записанную с паролем");
        Check.Expect(pages.All(trace => !trace.Key.Contains("secret") && !trace.Url.AbsoluteUri.Contains("secret") && !trace.Address.Contains("secret")),
            "ни идентичность, ни адрес не держат пароль");
        var written = Saved();
        Check.Expect(written.Contains("cred.example.test") && !written.Contains("secret"), "history.json не держит пароль");

        Check.World("legacy-credentials");
        Seed($$"""[{"url":"https://user:secret@oldcred.example.test/page","key":"oldcred.example.test/page","title":"Old","count":4,"last":{{Then}}}]""");
        var loaded = new History();
        Check.Expect(Traces(loaded, "oldcred.example.test") is [{ Count: 4 }], "старая запись с паролем переживает загрузку");
        written = Saved();
        Check.Expect(written.Contains("oldcred.example.test") && !written.Contains("secret"), "пароль из старого файла уходит сразу после загрузки");

        Check.World("huge-counts");
        Seed($$"""
            [{"url":"https://big.example.test/","key":"big.example.test","title":"Big","count":9223372036854775807,"last":{{Then}}},
             {"url":"https://big.example.test/#again","key":"big.example.test","title":"Big","count":9223372036854775807,"last":{{Then}}},
             {"url":"https://neg.example.test/","key":"neg.example.test","title":"Neg","count":-5,"last":{{Then}}}]
            """);
        var big = new History();
        big.Record(U("https://big.example.test/"), "Big");
        Check.Expect(Traces(big, "big.example.test") is [{ Count: History.MostVisits }], "счётчики из правленого файла ограничены и не переполняются");
        Check.Expect(Traces(big, "neg.example.test") is [{ Count: 0 }], "отрицательный счётчик становится нулём");
    }

    static void WwwCompletion()
    {
        Check.World("www-completion");
        var history = new History();
        history.Record(U("https://www.apple-test.example/iPhone"), "iPhone");
        var options = history.Suggestions("apple-t", 3);
        Check.Expect(History.Completion("apple-t", options) == "est.example", "сайт с www дописывается по имени");
        Check.Expect(History.Completion("www.apple-t", options) == "est.example", "и когда www набрали");
        var page = options.Where(option => option.Url.AbsolutePath == "/iPhone").ToList();
        Check.Expect(History.Completion("apple-test.example/iP", page) == "hone", "страница сайта с www дописывается без www");
        Check.Expect(History.Completion("apple-test.example/ip", page) is null, "без www регистр пути всё равно сохраняется");
        history.Record(U("http://www.plain-test.example/"), "Plain");
        Check.Expect(History.Completion("plain-t", history.Suggestions("plain-t", 3)) is null, "снятие www не превращает http в https");
    }

    static void OneLetter()
    {
        Check.World("one-letter");
        var history = new History();
        for (var i = 0; i < 5; i++) history.Record(U("https://x.com/"), "X");
        history.Record(U("https://x.com/home"), "Home");
        var options = history.Suggestions("x", 3);
        Check.Expect(options.FirstOrDefault()?.Key == "x.com", $"x.com — верхняя строка для «x», получено: {Kinds(options)}");
        Check.Expect(History.Completion("x", options) == ".com", "одна буква дописывает верхний посещённый сайт");
        Check.Expect(History.Completion("X", options) == ".com", "в любом регистре");
        Check.Expect(History.Completion("x", options.Where(option => option.Url.AbsolutePath == "/home").ToList()) is null, "одна буква не дописывает страницу внутри сайта");
        Check.Expect(History.Completion("q", history.Suggestions("q", 3)) is null, "одна буква не дописывает имя, которое программа просто знает");
        history.Record(U("https://netflix-test.example/"), "N");
        var inside = history.Suggestions("e", 3).Where(option => option.Key.StartsWith("netflix")).ToList();
        Check.Expect(History.Completion("e", inside) is null, "буква из середины имени ничего не дописывает");
        if (options.Count > 0)
        {
            List<Suggestion> lower = [new("q-test.example", "", U("https://q-test.example/"), Suggestion.Kinds.Known), options[0]];
            Check.Expect(History.Completion("x", lower) is null, "одна буква дописывает только верхнюю строку");
        }
        Check.Expect(History.Completion("x.", options) == "com", "две буквы дописывают как раньше");
    }

    static void TitlesPathsAndSlips()
    {
        Check.World("loose");
        var history = new History();
        history.Record(U("https://news.example-test.org/"), "Hacker Daily");
        history.Record(U("https://docs.example-test.org/guide/concurrency"), "Swift Guide");
        for (var i = 0; i < 3; i++) history.Record(U("https://hackerspace.example/"), "Space");

        var byTitle = history.Suggestions("daily");
        Check.Expect(byTitle.Any(option => option.Url.Host == "news.example-test.org"), $"слово заголовка находит страницу: {Kinds(byTitle)}");
        bool Guide(Suggestion option) => option.Url.AbsolutePath == "/guide/concurrency";
        Check.Expect(history.Suggestions("concurrency").Any(Guide), "слово пути находит страницу");
        Check.Expect(history.Suggestions("concurency").Any(Guide), "слово с опечаткой всё равно находит");
        Check.Expect(history.Suggestions("guide swift").Any(Guide), "все слова в любом порядке");
        Check.Expect(!history.Suggestions("guide kotlin").Any(Guide), "слово, которого нигде нет, ничего не находит");

        // Адрес как набран всегда первый, что бы ни говорил заголовок.
        var hacker = history.Suggestions("hacker");
        Check.Expect(hacker.FirstOrDefault()?.Url.Host == "hackerspace.example", $"адрес как набран идёт первым: {Kinds(hacker)}");
        Check.Expect(hacker.Any(option => option.Url.Host == "news.example-test.org"), "совпадение по заголовку предлагается под ним");
        Check.Expect(History.Completion("daily", byTitle) is null, "совпадению по заголовку нечего дописывать");
        Check.Expect(history.Suggestions("da").All(option => !option.Title.Contains("Daily")), "две буквы заголовок не ищут");
        Check.Expect(history.Suggestions("dai").Any(option => option.Title.Contains("Daily")), "три ищут");

        // счёт = совпадение × log(2 + популярность)
        Check.World("score-slip");
        var slip = new History();
        slip.Record(U("https://wiki.example/notes"), "Pipline notes");
        for (var i = 0; i < 40; i++) slip.Record(U("https://git.example/team/api/-/pipelines"), "Pipelines");
        var often = slip.Suggestions("pipline");
        Check.Expect(often.FirstOrDefault()?.Key == "git.example/team/api/-/pipelines", $"ежедневная страница с опечаткой обгоняет виденную раз: {Kinds(often)}");

        Check.World("score-tie");
        var tie = new History();
        tie.Record(U("https://git.example/team/api/-/pipelines"), "");
        tie.Record(U("https://wiki.example/notes"), "Pipline");
        var even = tie.Suggestions("pipline");
        Check.Expect(even.FirstOrDefault()?.Key == "wiki.example/notes", $"при равных визитах первым идёт совпадение как набрано: {Kinds(even)}");

        Check.World("score-many");
        var many = new History();
        for (var i = 0; i < 50; i++) many.Record(U("https://git.example/old/api"), "");
        for (var i = 0; i < 500; i++) many.Record(U("https://git.example/team/api"), "");
        var more = many.Suggestions("api");
        Check.Expect(more.FirstOrDefault()?.Key == "git.example/team/api", $"500 визитов обгоняют 50: {Kinds(more)}");

        // Странице засчитываются визиты всех страниц под ней.
        Check.World("score-under");
        var under = new History();
        for (var i = 0; i < 5; i++) under.Record(U("https://git.example/team/api"), "team / api");
        for (var i = 0; i < 40; i++) under.Record(U("https://git.example/team/api/-/merge_requests/1"), "Fix api");
        for (var i = 0; i < 20; i++) under.Record(U("https://git.example/team/api/-/pipelines/2"), "Pipeline · api");
        under.Record(U("https://git.example/team/api-old/-/merge_requests/9"), "Old api");
        for (var i = 0; i < 5; i++) under.Record(U("https://rapid.example/"), "Rapid");
        var repo = under.Suggestions("api", 3);
        Check.Expect(repo is [{ Key: "git.example/team/api" }, { Key: "git.example/team/api/-/merge_requests/1" }, ..],
            $"репозиторий идёт перед своими страницами, затем самая посещаемая из них: {Kinds(repo)}");

        // При равных визитах решает совпадение.
        Check.World("score-fit");
        var fits = new History();
        foreach (var address in (string[])["https://api.example/", "https://dev-api.example/", "https://git.example/team/api", "https://rapid.example/"])
            fits.Record(U(address), "");
        var fit = fits.Suggestions("api").Select(option => option.Key).ToList();
        Check.Expect(fit is ["api.example", "dev-api.example", ..], $"начало адреса, затем начало слова в имени: {string.Join(", ", fit)}");
        Check.Expect(fit.IndexOf("git.example/team/api") is var path && path >= 0 && path < fit.IndexOf("rapid.example"),
            $"слово пути раньше слова, в котором оно спрятано: {string.Join(", ", fit)}");

        // Известное имя с опечаткой, когда рядом нет ничего посещённого.
        Check.World("loose-known");
        Check.Expect(new History().Suggestions("githb").Any(option => option.Key == "github.com"), "известный сайт с опечаткой предлагается");
    }

    /// Заголовок, который всё время меняется (почта считает письма), ждёт
    /// следующей записи; первый заголовок страницы пишется сразу.
    static void TitleChurn()
    {
        Check.World("title-churn");
        var file = Store.File("history.json");
        var history = new History();
        var inbox = U("https://mail.example.test/inbox");
        history.Record(inbox, "");
        Check.Expect(Saved().Contains("mail.example.test"), "визит записывается после паузы");
        File.Delete(file);

        history.Retitle(inbox, "(1) Inbox");
        Check.Expect(Saved().Contains("(1) Inbox"), "первый заголовок страницы записывается");
        File.Delete(file);

        history.Retitle(inbox, "(2) Inbox");
        history.Retitle(inbox, "(3) Inbox");
        Check.Expect(Saved().Length == 0, "заголовок, меняющийся снова, файл не переписывает");

        history.Flush();
        Check.Expect(File.Exists(file) && File.ReadAllText(file).Contains("(3) Inbox"), "выход записывает последний заголовок");
    }

    /// Память и файл не растут без конца, а страница, на которой вы сейчас, не пропадает.
    static void Capped()
    {
        Check.World("capped");
        var history = new History();
        for (var i = 0; i < 20; i++) history.Record(U("https://often.example/daily"), "Daily");
        var total = History.Cap + History.Slack + 100;
        for (var i = 0; i < total; i++) history.Record(U($"https://cap.example/p{i}"), "Page");

        var all = history.Everything();
        Check.Expect(all.Count <= History.Cap + History.Slack, $"память обрезается: {all.Count} мест");
        Check.Expect(all.FirstOrDefault()?.Url.AbsolutePath == $"/p{total - 1}", "самая новая страница остаётся");
        Check.Expect(all.Any(trace => trace.Url.Host == "often.example"), "часто посещаемая страница остаётся, хоть она и старше");

        history.Flush();
        Check.Expect(new History().Everything().Count <= History.Cap, "файл хранит не больше предела");
    }
}
