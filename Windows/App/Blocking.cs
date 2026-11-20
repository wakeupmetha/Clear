using Microsoft.Web.WebView2.Core;

namespace Browser;

// Блокировщик, подключённый к движку. Сами правила — в Core/Shield.cs.
//
// На macOS WebKit применяет список правил внутри своей сети. У WebView2 есть
// только событие «запрошен ресурс», и каждое такое событие — поход из движка в
// оболочку и обратно. Поэтому движку заранее отдаются фильтры по доменам из
// списка: в оболочку приходят только запросы к ним, а все остальные идут как
// шли, без задержки.
//
// ponytail: по фильтру на запись списка, их около сотни. Для списков в тысячи
// правил (EasyList) этот путь не годится: нужно расширение с
// declarativeNetRequest, которое работает внутри сети движка.
static class Blocking
{
    /// Сколько запросов остановлено с запуска. Для проверок через тест-канал:
    /// в окне счётчика нет и не будет, как в апстриме.
    public static int Blocked { get; private set; }

    /// Ставит защиту на движок вкладки. Дождаться до первой загрузки: иначе
    /// первая страница успеет начаться без неё.
    public static async Task Protect(Tab tab, CoreWebView2 core)
    {
        foreach (var entry in Shield.Unwanted)
        {
            // Шире самого правила (подойдёт и notdoubleclick.net): точно
            // решает Shield.Blocks, фильтр только отсеивает заведомо лишнее.
            // У записи с путём («yandex.ru/ads/») путь уже внутри.
            core.AddWebResourceRequestedFilter(entry.Contains('/') ? $"*://*{entry}*" : $"*://*{entry}/*",
                CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        }
        core.WebResourceRequested += (_, asked) =>
        {
            // Вкладка сама идёт на такой сайт: по ссылке или по набранному
            // адресу. Это не чужой запрос, и останавливать его нельзя: иначе
            // переход по рекламной ссылке кончался бы страницей ошибки.
            if (asked.ResourceContext == CoreWebView2WebResourceContext.Document
                && Plain(asked.Request.Uri) == tab.Heading) return;
            if (!Uri.TryCreate(asked.Request.Uri, UriKind.Absolute, out var request)) return;
            // Чужой или свой — считается от страницы вкладки, а не от рамки,
            // из которой пришёл запрос: так же, как в правилах WebKit.
            var page = Uri.TryCreate(core.Source, UriKind.Absolute, out var source) ? source.Host : null;
            if (!Shield.Blocks(request, page)) return;
            asked.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            Blocked++;
        };
        await Dress(tab, core);
    }

    /// Ставит или снимает скрипт, прячущий рекламные места, по нынешнему
    /// состоянию блокировщика. Вызывать снова, когда оно меняется.
    public static async Task Dress(Tab tab, CoreWebView2 core)
    {
        if (tab.ShieldScript is { } old)
        {
            core.RemoveScriptToExecuteOnDocumentCreated(old);
            tab.ShieldScript = null;
        }
        if (Shield.Script() is { Length: > 0 } script)
            tab.ShieldScript = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
    }

    /// Адрес без фрагмента: в запросе его нет, в адресе перехода он есть.
    public static string Plain(string address)
    {
        var hash = address.IndexOf('#');
        return hash < 0 ? address : address[..hash];
    }
}
