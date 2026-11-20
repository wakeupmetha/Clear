using System.Text.Json;

namespace Browser;

// Перенос данных и правил из Sources/Search/Shield.swift.
//
// На macOS это список правил, который WebKit применяет внутри своей сети. У
// WebView2 такого списка нет. Здесь правила: кого блокировать (Blocks) и что
// прятать (Script). К движку их подключает App/Blocking.cs.
public static class Shield
{
    /// Включён, пока его не выключили. Ключи настроек те же, что в апстриме.
    public static bool Enabled
    {
        get => Settings.Bool("shield", true);
        set => Settings.Set("shield", value);
    }

    /// Сайты, на которых блокировщик выключен: те, что он сломал. Выключить
    /// здесь лучше, чем выключить везде и забыть включить обратно.
    ///
    /// Отличие от апстрима: запись действует и на поддомены. Там сайт
    /// выключают из карточки вкладки и записывают её хост как есть; здесь
    /// список набирают руками в настройках, и «example.com» должен значить и
    /// «www.example.com».
    ///
    /// ponytail: список читается из настроек на каждый проверяемый запрос (их
    /// десятки на страницу, записей единицы). При сотнях сайтов держать
    /// готовое множество и обновлять его по Settings.Changed.
    public static string[] Paused => Settings.List("shield.paused");

    /// Блокирует uBlock Origin из установки: свой список тогда молчит, а
    /// переключатель «shield» включает и выключает uBlock (решение владельца
    /// 2026-11-20: один блокировщик, без двойной работы).
    public static bool Delegated { get; set; }

    /// Сайт, как его набрали в настройках: имя или адрес целиком. Null, если
    /// это не сайт.
    public static string? Named(string typed) =>
        Address.Url(typed) is { Host.Length: > 0, Scheme: "http" or "https" } url ? Address.WithoutWWW(url.Host) : null;

    public static void Pause(string host, bool off)
    {
        var others = Paused.Where(site => site != host);
        Settings.Set("shield.paused", off ? others.Append(host).Order() : others);
    }

    /// Выключен ли блокировщик на странице этого хоста: записью для него или
    /// для сайта выше («example.com» выключает и «www.example.com»).
    public static bool PausedOn(string host) => Paused.Any(site => Under(host, site));

    /// Включить на этом хосте из карточки сайта: убрать и запись сайта выше —
    /// иначе переключатель остался бы выключенным.
    public static void Resume(string host) =>
        Settings.Set("shield.paused", Paused.Where(site => !Under(host, site)));

    /// Всё из списка: домены («doubleclick.net») и адреса с путём
    /// («yandex.ru/ads/»). Движку, чтобы он отдавал на проверку только
    /// запросы к ним.
    public static IReadOnlyCollection<string> Unwanted =>
        [.. Listed, .. ListedPaths.Select(rule => rule.Host + rule.Prefix)];

    // Третьи стороны, чья единственная работа — следить или продавать.
    static readonly HashSet<string> Listed =
    [
        // Список апстрима.
        "doubleclick.net", "googlesyndication.com", "googleadservices.com",
        "googletagservices.com", "google-analytics.com", "googletagmanager.com",
        "adservice.google.com", "amazon-adsystem.com", "adnxs.com", "adsrvr.org",
        "criteo.com", "criteo.net", "taboola.com", "outbrain.com",
        "rubiconproject.com", "pubmatic.com", "openx.net", "casalemedia.com",
        "smartadserver.com", "sharethrough.com", "indexww.com", "bidswitch.net",
        "33across.com", "teads.tv", "moatads.com", "adroll.com",
        "scorecardresearch.com", "quantserve.com", "chartbeat.com",
        "hotjar.com", "mouseflow.com", "fullstory.com", "clarity.ms",
        "mixpanel.com", "amplitude.com", "segment.com", "segment.io",
        "branch.io", "appsflyer.com", "adjust.com", "analytics.tiktok.com",
        "connect.facebook.net", "ads-twitter.com", "analytics.twitter.com",

        // Российские сети. В апстриме их нет: он собирался под другую
        // аудиторию. Состав — по обходу двенадцати крупных сайтов 20.11.2026:
        // что они запрашивают у третьих сторон. Поддомены крупных порталов
        // названы поимённо: остальной mail.ru и rambler.ru — не реклама.
        "an.yandex.ru", "yandexadexchange.net", "adfox.ru",
        "ad.mail.ru", "mradx.net", "privacy-cs.mail.ru",
        "ssp.rambler.ru", "ssp01.rambler.ru", "mpx.rambler.ru", "mpx2.rambler.ru",
        "pixel.rambler.ru", "dsp-rambler.ru",
        "adriver.ru", "betweendigital.com", "buzzoola.com", "al-adtech.com",
        "bumlam.com", "otm-r.com", "skcrtxr.com", "gnezdo.ru", "sape.ru",
        "ad-tech.ru", "rutarget.ru", "adhigh.net", "ohmy.bid", "smi2.ru",
        "acint.net", "videonow.ru", "hybrid.ai", "bidderstack.com",
        "weborama-tech.ru", "aidata.io", "24smi.net", "a.mts.ru", "aio.media",
        "linkssp.ru", "serving-sys.ru", "viadata.store", "bidvol.com",
        "alfasense.com", "mediatoday.ru", "mgid.com", "relap.io",
        // Счётчики.
        "mc.yandex.ru", "mc.yandex.md", "mc.yandex.com", "mc.yandex.by", "mc.yandex.kz",
        "mc.webvisor.org", "counter.yadro.ru", "tns-counter.ru", "top-fwz1.mail.ru",
        "top100.ru", "kraken.rambler.ru", "kraken-mdt.rambler.ru",
    ];

    // Рекламный код на общем домене. Заблокировать yandex.ru или vk.ru целиком
    // нельзя: на них же карты, вход и виджеты. Поэтому домен вместе с началом
    // пути. Загрузчик рекламы Яндекса стоял на всех двенадцати сайтах обхода.
    static readonly (string Host, string Prefix)[] ListedPaths =
    [
        ("yandex.ru", "/ads/"), ("yandex.ru", "/adfox/"),
        ("yastatic.net", "/partner-code-bundles/"), ("yastatic.net", "/pcode/"),
        ("vk.ru", "/rtrg"), ("vk.com", "/rtrg"),
    ];

    // Места, которые надёжно реклама и ничего больше. Список намеренно
    // короткий: щедрый косметический список начинает есть саму страницу.
    static readonly string[] Slots =
    [
        ".adsbygoogle", "ins.adsbygoogle", "[id^=\"google_ads_\"]",
        "[id^=\"div-gpt-ad\"]", "[id^=\"taboola-\"]", "#taboola-below-article",
        "iframe[src*=\"doubleclick.net\"]", "iframe[src*=\"googlesyndication\"]",
        "iframe[src*=\"amazon-adsystem\"]",
        ".ad-slot", ".ad-slot-container", ".top-banner-ad-container",
        ".ad-leaderboard", ".ad-billboard", ".ad-giga", ".ad-mpu", ".ad-mrec",
        ".ad-unit", ".adunit", ".adslot", ".dfp-ad", ".gpt-ad", ".w_ad",
        // Контейнеры рекламы Яндекса и Adfox: без загрузчика они остаются
        // пустыми рамками.
        "[id^=\"yandex_rtb_\"]", "[id^=\"adfox_\"]",
    ];

    // Имена слишком простые, чтобы прятать везде: только на своих сайтах.
    static readonly (string[] Sites, string Selector)[] SlotsBySite =
    [
        (["as.com", "elpais.com"], ".ad"),
        (["theguardian.com"], ".top-fronts-banner-ad-container"),
        (["independent.co.uk", "the-independent.com"], "#billboard-wrapper"),
        (["cnn.com"], ".ad-slot-header__wrapper"),
    ];

    /// Блокировать ли запрос со страницы на pageHost. Запросы сайта к самому
    /// себе не трогаем: собственные скрипты сайта — это и есть сайт.
    public static bool Blocks(Uri request, string? pageHost)
    {
        if (!Enabled || Delegated || request.Scheme is not ("http" or "https")) return false;
        if (pageHost is not null && PausedOn(pageHost)) return false;
        var host = request.Host.AsSpan();
        if (pageHost is not null && Site(host).SequenceEqual(Site(pageHost))) return false;

        // Сам домен и любой его поддомен.
        var listed = Listed.GetAlternateLookup<ReadOnlySpan<char>>();
        for (var rest = host; ;)
        {
            if (listed.Contains(rest)) return true;
            var dot = rest.IndexOf('.');
            if (dot < 0) break;
            rest = rest[(dot + 1)..];
        }
        foreach (var (site, prefix) in ListedPaths)
        {
            if (Under(host, site) && request.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    static bool Under(ReadOnlySpan<char> host, string site) =>
        host.SequenceEqual(site) || (host.EndsWith(site) && host.Length > site.Length && host[^(site.Length + 1)] == '.');

    // ponytail: две последние метки вместо настоящего регистрируемого домена.
    // Для нынешнего списка это точно; с доменами под суффиксами вида co.uk
    // понадобится список публичных суффиксов. Побочное следствие: страницы
    // самого портала (mail.ru, yandex.ru) свои счётчики и рекламу сохраняют.
    static ReadOnlySpan<char> Site(ReadOnlySpan<char> host)
    {
        var last = host.LastIndexOf('.');
        if (last <= 0) return host;
        var previous = host[..last].LastIndexOf('.');
        return previous < 0 ? host : host[(previous + 1)..];
    }

    /// Скрипт, который прячет рекламные места. Движок выполняет его при
    /// создании каждого документа, до разбора страницы: ничего не успевает
    /// появиться и исчезнуть. Пусто, когда блокировщик выключен.
    ///
    /// Какие места прятать на каком сайте, решает сам скрипт: он один на все
    /// страницы и регистрируется заранее, а выбрать стили под сайт из кода
    /// приложения к началу загрузки можно не успеть. Таблица стилей создаётся
    /// без элемента в документе: в этот момент у документа ещё нет корня.
    public static string Script()
    {
        if (!Enabled || Delegated) return "";
        static string Json(string[] values) => JsonSerializer.Serialize(values, Browser.Json.Default.StringArray);
        var bySite = SlotsBySite.Select(entry => (string[])[entry.Selector, .. entry.Sites]).ToArray();
        return $$"""
            (() => {
              const host = location.hostname;
              const under = site => host === site || host.endsWith("." + site);
              if ({{Json(Paused)}}.some(under)) return;
              const hidden = {{Json(Slots)}};
              for (const [selector, ...sites] of {{JsonSerializer.Serialize(bySite, Browser.Json.Default.StringArrayArray)}})
                if (sites.some(under)) hidden.push(selector);
              const sheet = new CSSStyleSheet();
              sheet.replaceSync(hidden.join(", ") + " { display: none !important; }");
              document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
            })();
            """;
    }
}
