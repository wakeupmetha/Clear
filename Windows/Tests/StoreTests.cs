using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Browser.Tests;

static class StoreTests
{
    public static void Run()
    {
        Writes();
        Sessions();
        Kept();
        Blocking();
        Adopting();
    }

    static void Adopting()
    {
        var root = Path.Combine(Path.GetTempPath(), $"store-adopt-{Environment.ProcessId}");
        string now = Path.Combine(root, "Clear"), former = Path.Combine(root, "aerio");
        void Fresh() { if (Directory.Exists(root)) Directory.Delete(root, true); Directory.CreateDirectory(root); }

        Fresh();
        Check.Expect(Store.Adopt(now, former) == now && !Directory.Exists(former), "ни новой, ни прежней — новая");

        Fresh();
        Directory.CreateDirectory(former);
        File.WriteAllText(Path.Combine(former, "session.json"), "{}");
        Check.Expect(Store.Adopt(now, former) == now && File.Exists(Path.Combine(now, "session.json")) && !Directory.Exists(former),
            "прежняя переезжает под новое имя вместе с файлами");

        Fresh();
        Directory.CreateDirectory(now);
        Directory.CreateDirectory(former);
        Check.Expect(Store.Adopt(now, former) == now && Directory.Exists(former), "новая уже есть — прежнюю не трогаем");

        // Прежний браузер ещё открыт и держит файл: переезда нет, данные в прежней папке.
        Fresh();
        Directory.CreateDirectory(former);
        using (File.Open(Path.Combine(former, "held"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Check.Expect(Store.Adopt(now, former) == former && !Directory.Exists(now), "занятая прежняя — работаем в ней, новой нет");
        Check.Expect(Store.Adopt(now, former) == now, "освободилась — переезжает при следующем запуске");

        Directory.Delete(root, true);
    }

    static void Kept()
    {
        Check.World("settings");
        Check.Expect(!Settings.Bool("sidebar.folded") && Settings.Bool("shield.enabled", true), "без файла настройки берут значения по умолчанию");
        Check.Expect(Settings.Text("engine") is null && Settings.List("shield.paused").Length == 0, "и текст со списком тоже");

        Settings.Set("sidebar.folded", true);
        Settings.Set("engine", "duckduckgo");
        Settings.Set("shield.paused", ["news.example", "shop.example"]);
        Settings.Set("sleep.after", 2.5);
        Check.Expect(Settings.Bool("sidebar.folded") && Settings.Text("engine") == "duckduckgo", "настройка читается сразу после записи");
        Check.Expect(Settings.List("shield.paused") is ["news.example", "shop.example"], "список хранит порядок");
        Check.Expect(Settings.Text("sidebar.folded") is null && !Settings.Bool("engine"), "значение не того типа читается как отсутствующее");

        // Как перезапуск: уходим в другой мир и возвращаемся, настройки читаются с диска.
        Disk.Drain();
        Check.World("settings-elsewhere");
        Check.Expect(!Settings.Bool("sidebar.folded"), "у другого мира настройки свои");
        Check.World("settings");
        Check.Expect(Settings.Bool("sidebar.folded") && Settings.List("shield.paused").Length == 2, "настройки переживают перезапуск");
        Check.Expect(Settings.Number("sleep.after") == 2.5 && Settings.Number("engine") == 0, "число — тоже; не число читается как 0");

        Settings.Remove("sidebar.folded");
        Check.Expect(!Settings.Bool("sidebar.folded"), "удалённая настройка возвращается к значению по умолчанию");

        Disk.Drain();
        Check.World("settings-broken");
        Directory.CreateDirectory(Store.Folder);
        File.WriteAllText(Store.File("settings.json"), "[1, 2");
        Check.Expect(!Settings.Bool("sidebar.folded") && Directory.GetFiles(Store.Folder, "settings.unreadable-*.json").Length == 1,
            "нечитаемый файл настроек отложен в сторону, настройки по умолчанию");
    }

    static void Writes()
    {
        Check.World("disk");
        Check.Expect(Store.Testing && Store.World == "test", "запуск из папки сборки — тестовый мир");

        // Сессия при выходе не должна быть затёрта более старой, ещё бывшей в пути.
        var file = Store.File("order.json");
        using (var held = new ManualResetEventSlim())
        {
            Disk.Write(Store.File("slow.json"), () => { held.Wait(); return "slow"u8.ToArray(); });
            Disk.Write(file, () => "old"u8.ToArray());
            Disk.Write(file, () => "new"u8.ToArray(), now: true);
            held.Set();
            Disk.Drain();
        }
        Check.Expect(File.ReadAllText(file) == "new", "запись, которую обогнала более новая, отбрасывается");
        Check.Expect(Directory.GetFiles(Store.Folder, "*.writing").Length == 0, "временных файлов не остаётся");

        Disk.Write(file, () => "again"u8.ToArray());
        Disk.Remove(file);
        Disk.Drain();
        Check.Expect(!File.Exists(file), "удаление идёт после записей, запрошенных до него");
    }

    static void Sessions()
    {
        Check.World("session");
        Check.Expect(Session.Read().Tabs.Count == 0, "нет файла — пустая сессия");

        var touched = new DateTime(2026, 11, 19, 12, 30, 0, DateTimeKind.Utc);
        Session.Write(new Session.Shape
        {
            Tabs =
            [
                new() { Url = "https://example.com/", Title = "Example", Pin = "E", Home = "https://example.com/", Touched = touched },
                new() { Url = "https://news.example/a", Title = "Новости", Name = "Утро", Muted = true },
            ],
            Active = 1,
        }, now: true);
        var back = Session.Read();
        Check.Expect(back.Active == 1 && back.Tabs.Count == 2, "сессия возвращается целиком");
        Check.Expect(back.Tabs[0] is { Pin: "E", Home: "https://example.com/" } && back.Tabs[0].Touched == touched, "закреплённая вкладка помнит букву, дом и время");
        Check.Expect(back.Tabs[1] is { Title: "Новости", Name: "Утро", Muted: true, Pin: null }, "обычная вкладка помнит имя и звук");

        // Файл версии для macOS: даты числом секунд от 2001 года, плюс ключи,
        // которых здесь пока нет.
        File.WriteAllText(Store.File("session.json"),
            """{"tabs":[{"url":"https://example.com/","title":"Example","touched":721692800.5,"muted":false,"groupID":"E621E1F8-C36C-495A-93FC-0C247A3E6E5F"}],"active":0,"splits":[]}""");
        var mac = Session.Read();
        Check.Expect(mac.Tabs.Count == 1 && mac.Tabs[0].Touched == DateTime.UnixEpoch.AddSeconds(1_700_000_000.5), "session.json с Mac читается как есть");

        File.WriteAllText(Store.File("session.json"), "{\"tabs\": oops");
        Check.Expect(Session.Read().Tabs.Count == 0, "нечитаемый файл — пустая сессия");
        Check.Expect(!File.Exists(Store.File("session.json")) && Directory.GetFiles(Store.Folder, "session.unreadable-*.json").Length == 1,
            "нечитаемый файл отложен в сторону, а не затёрт");
    }

    static void Blocking()
    {
        Check.World("shield-rules");
        Shield.Delegated = true;
        Check.Expect(!Shield.Blocks(new Uri("https://stats.g.doubleclick.net/x.js"), "news.example") && Shield.Script() == "",
            "когда блокирует uBlock, свой список молчит: ни запросов, ни скрипта");
        Check.Expect(Shield.Blocks(new Uri("https://stats.g.doubleclick.net/x.js"), "news.example", alone: true) && Shield.Script(alone: true).Length > 0,
            "а у страницы без uBlock (приватной) — работает");
        Shield.Delegated = false;
        Check.Expect(Shield.Blocks(new Uri("https://stats.g.doubleclick.net/x.js"), "news.example"), "поддомен рекламной сети блокируется");
        Check.Expect(Shield.Blocks(new Uri("https://doubleclick.net/x.js"), null), "и когда страница неизвестна");
        Check.Expect(!Shield.Blocks(new Uri("https://example.com/app.js"), "example.com"), "собственные скрипты сайта не трогаем");
        Check.Expect(!Shield.Blocks(new Uri("https://cdn.example.net/app.js"), "example.com"), "посторонний, но не рекламный запрос проходит");
        Check.Expect(!Shield.Blocks(new Uri("https://notdoubleclick.net/x.js"), "news.example"), "похожее имя не домен из списка");
        Check.Expect(!Shield.Blocks(new Uri("https://ads.doubleclick.net/x.js"), "www.doubleclick.net"), "запрос сайта к самому себе не блокируется");
        Check.Expect(!Shield.Blocks(new Uri("https://adservice.google.com/x.js"), "www.google.com"), "тот же сайт под другим поддоменом — свой");

        // Российские сети: домены и рекламный код на общих доменах.
        Check.Expect(Shield.Blocks(new Uri("https://mc.yandex.ru/metrika/tag.js"), "habr.com"), "счётчик на чужом сайте блокируется");
        Check.Expect(Shield.Blocks(new Uri("https://ads.betweendigital.com/adjson"), "www.gazeta.ru"), "рекламная биржа блокируется");
        Check.Expect(Shield.Blocks(new Uri("https://yandex.ru/ads/system/context.js"), "habr.com"), "загрузчик рекламы на общем домене блокируется по пути");
        Check.Expect(Shield.Blocks(new Uri("https://yastatic.net/partner-code-bundles/1307478/a.js"), "lenta.ru"), "и его код на общем хранилище");
        Check.Expect(!Shield.Blocks(new Uri("https://yastatic.net/s3/home/fonts/ys/3/text.woff2"), "lenta.ru"), "шрифты с того же хранилища проходят");
        Check.Expect(!Shield.Blocks(new Uri("https://api-maps.yandex.ru/2.1/?lang=ru_RU"), "shop.example"), "карты Яндекса проходят");
        Check.Expect(!Shield.Blocks(new Uri("https://yandex.ru/ads/system/context.js"), "yandex.ru"), "на самом портале его реклама не трогается");
        Check.Expect(Shield.Blocks(new Uri("https://vk.ru/rtrg?p=VK-RTRG-1"), "www.kp.ru"), "метка ретаргетинга блокируется");
        Check.Expect(!Shield.Blocks(new Uri("https://vk.ru/js/api/openapi.js"), "www.kp.ru"), "виджеты с того же домена проходят");
        Check.Expect(!Shield.Blocks(new Uri("https://notyandex.ru/ads/x.js"), "habr.com"), "похожее имя с тем же путём не из списка");
        Check.Expect(!Shield.Blocks(new Uri("https://vp.rambler.ru/player/sdk.js"), "lenta.ru"), "видеоплеер портала проходит");
        Check.Expect(Shield.Unwanted.Contains("yandex.ru/ads/") && Shield.Unwanted.Contains("doubleclick.net"), "движку отдаются и домены, и адреса с путём");

        Shield.Pause("news.example", true);
        Shield.Pause("a.example", true);
        Shield.Pause("news.example", true);
        Check.Expect(Shield.Paused is ["a.example", "news.example"], "сайты, где выключено, хранятся по алфавиту и без повторов");
        Check.Expect(!Shield.Blocks(new Uri("https://doubleclick.net/x.js"), "news.example"), "на сайте, где выключено, ничего не блокируется");
        Check.Expect(!Shield.Blocks(new Uri("https://doubleclick.net/x.js"), "www.news.example"), "и на его поддомене");
        Check.Expect(Shield.Blocks(new Uri("https://doubleclick.net/x.js"), "goodnews.example"), "а на сайте с похожим именем блокируется");
        // Как перезапуск: настройки читаются с диска.
        Disk.Drain();
        Check.World("shield-elsewhere");
        Check.Expect(Shield.Paused.Length == 0, "у другого мира список свой");
        Check.World("shield-rules");
        Check.Expect(Shield.Paused.Length == 2, "список сайтов переживает перезапуск");
        Shield.Pause("news.example", false);
        Shield.Pause("a.example", false);
        Check.Expect(Shield.Paused.Length == 0 && Shield.Blocks(new Uri("https://doubleclick.net/x.js"), "news.example"), "сайт возвращается под блокировщик");

        Shield.Pause("news.example", true);
        Shield.Pause("a.example", true);
        Check.Expect(Shield.PausedOn("www.news.example") && Shield.PausedOn("news.example") && !Shield.PausedOn("goodnews.example") && !Shield.PausedOn("example"),
            "выключен ли блокировщик на сайте — по записи для него или для сайта выше");
        Shield.Resume("www.news.example");
        Check.Expect(Shield.Paused is ["a.example"], "включить на поддомене — убрать и запись сайта выше, остальные остаются");
        Shield.Pause("a.example", false);

        Check.Expect(Shield.Named(" https://www.Habr.com/ru/feed/ ") == "habr.com" && Shield.Named("lenta.ru") == "lenta.ru", "сайт набирают именем или адресом целиком");
        Check.Expect(Shield.Named("two words") is null && Shield.Named("edge://flags") is null && Shield.Named("") is null, "не сайт в список не попадает");

        var heard = new List<string>();
        void Hear(string key) => heard.Add(key);
        Settings.Changed += Hear;
        Shield.Enabled = false;
        Check.Expect(Shield.Script() == "" && !Shield.Blocks(new Uri("https://doubleclick.net/x.js"), "news.example"),
            "выключенный блокировщик не блокирует и не прячет");
        Shield.Enabled = true;
        Settings.Changed -= Hear;
        Check.Expect(heard is ["shield", "shield"], "о смене настройки узнают те, кто слушает");

        Searching();
        Hiding();
    }

    /// Поисковик из настроек: ключи и значения как в апстриме.
    static void Searching()
    {
        Check.Expect(Engines.Chosen == Engine.Google && Engines.Current == Engine.Google.Template(), "без настройки ищет стандартный поисковик");
        Settings.Set("search.engine", Engine.DuckDuckGo.Key());
        Check.Expect(Settings.Text("search.engine") == "duckduckgo" && Engines.Current == "https://duckduckgo.com/?q=%s", "выбранный поисковик хранится строчными, как на Mac");
        Settings.Set("search.engine", "custom");
        Check.Expect(Engines.Current == Engine.Google.Template(), "свой поисковик без шаблона уходит в стандартный");
        Settings.Set("search.custom", "search.example/?q={searchTerms}");
        Check.Expect(Engines.Url("a b", Engines.Current)?.AbsoluteUri == "https://search.example/?q=a%20b", "свой шаблон из настроек работает");
        foreach (var junk in (string[])["yahoo", "42", ""])
        {
            Settings.Set("search.engine", junk);
            Check.Expect(Engines.Chosen == Engine.Google, $"непонятное значение «{junk}» — стандартный поисковик");
        }
        Settings.Remove("search.engine");
        Settings.Remove("search.custom");
    }

    /// Скрипт, прячущий рекламные места, выполняется в Node с подставленным
    /// документом: какие стили он ставит на каком сайте.
    static void Hiding()
    {
        Shield.Pause("paused.example", true);
        var script = Shield.Script();
        Shield.Pause("paused.example", false);
        Check.Expect(script.Contains(".adsbygoogle") && script.Contains("paused.example"), "скрипт несёт список мест и сайты, где выключено");

        const string harness = """
            const code = require('fs').readFileSync(process.argv[1], 'utf8');
            const out = {};
            for (const host of ['news.example', 'www.elpais.com', 'gas.com', 'paused.example', 'm.paused.example', 'notpaused.example']) {
              globalThis.location = { hostname: host };
              globalThis.CSSStyleSheet = class { replaceSync(text) { this.text = text; } };
              globalThis.document = { adoptedStyleSheets: [] };
              (0, eval)(code);
              out[host] = document.adoptedStyleSheets.map(sheet => sheet.text).join('');
            }
            console.log(JSON.stringify(out));
            """;
        Check.World("shield");
        Directory.CreateDirectory(Store.Folder);
        File.WriteAllText(Store.File("shield.js"), script);
        if (Said("node", "-e", harness, Store.File("shield.js")) is not { } said) return;
        if (!said.StartsWith('{'))
        {
            Check.Expect(false, "скрипт выполняется в Node: " + said);
            return;
        }
        using var styles = System.Text.Json.JsonDocument.Parse(said);
        string On(string host) => styles.RootElement.GetProperty(host).GetString() ?? "";
        Check.Expect(On("news.example").StartsWith(".adsbygoogle, ") && On("news.example").EndsWith("[id^=\"adfox_\"] { display: none !important; }"), "общие рекламные места прячутся везде");
        Check.Expect(On("www.elpais.com").EndsWith(", .ad { display: none !important; }"), "простое имя прячется на своём сайте");
        Check.Expect(!On("gas.com").Contains(", .ad {"), "и не прячется на сайте с похожим именем");
        Check.Expect(On("paused.example") == "" && On("m.paused.example") == "", "на сайте, где выключено, и на его поддомене ничего не прячется");
        Check.Expect(On("notpaused.example") != "", "а на сайте с похожим именем прячется");
    }

    /// Тест-канал: три запроса подряд из .NET и один из Python, ради которого
    /// транспорт и выбран.
    public static async Task BenchAnswers()
    {
        Check.World("bench");
        Check.Expect(Bench.Pipe == "bench.bench", "имя канала следует за папкой хранилища");

        var pipe = "browser-core-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var serving = Bench.Serve(pipe, request => Task.FromResult<JsonNode?>(
            new JsonObject { ["echo"] = request.GetProperty("do").GetString() }), stop.Token);

        async Task<string?> Ask(string line)
        {
            await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000);
            await client.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
            using var reader = new StreamReader(client, leaveOpen: true);
            return await reader.ReadLineAsync();
        }

        for (var round = 0; round < 3; round++)
            Check.Expect(await Ask("""{"do":"tabs"}""") == """{"echo":"tabs"}""", $"запрос {round + 1} подряд получает ответ");
        Check.Expect(await Ask("not json") == """{"error":"not json"}""", "мусор получает ответ с ошибкой");
        Check.Expect((await Ask("{}"))?.StartsWith("""{"error":""") == true, "ошибка в команде — ответ, а не конец сокета");
        Check.Expect(await Ask("""{"do":"still"}""") == """{"echo":"still"}""", "после ошибок сокет жив");

        if (OperatingSystem.IsWindows()) Check.Expect(FromPython(pipe) is "tabs" or null, "клиент на Python получает ответ через канал");

        stop.Cancel();
        await serving;
        Check.Expect(serving.IsCompletedSuccessfully, "остановка завершает обслуживание без ошибки");
    }

    // Клиент ровно такой, каким его придётся написать в bench для Windows.
    // Null, если Python не установлен: тогда проверять нечем.
    static string? FromPython(string pipe)
    {
        const string script = """
            import json, sys
            with open(r'\\.\pipe' + '\\' + sys.argv[1], 'r+b', buffering=0) as s:
                s.write(b'{"do":"tabs"}\n')
                line = b''
                while not line.endswith(b'\n'):
                    chunk = s.read(65536)
                    if not chunk:
                        break
                    line += chunk
            print(json.loads(line)['echo'])
            """;
        return Said("python", "-c", script, pipe);
    }

    /// Что напечатала чужая программа. Null, если её нет на этой машине:
    /// проверка, которой она нужна, тогда пропускается и говорит об этом.
    static string? Said(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var running = Process.Start(start);
            if (running is null) return null;
            var said = running.StandardOutput.ReadToEnd().Trim();
            var errors = running.StandardError.ReadToEnd().Trim();
            running.WaitForExit();
            return running.ExitCode == 0 ? said : $"{program}: {errors}";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"пропущено: {program} не найден, проверка с ним не выполнена");
            return null;
        }
    }
}
