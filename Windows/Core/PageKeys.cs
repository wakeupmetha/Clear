namespace Browser;

/// Сочетания окна, нажатые, пока фокус с клавиатуры в странице. Нажатия в
/// странице забирает движок, и до сочетаний окна (KeyboardAccelerators) они
/// не доходят. Поэтому скрипт в каждой странице ловит эти сочетания и
/// сообщает о них окну.
///
/// Какие сочетания ловить, решает Shortcuts (Page): скрипт строится из таблицы
/// команд, так что сочетание, которое человек переназначил, ловится и в странице.
///
/// Сайт не должен уметь сам закрыть вкладку или открыть новые. Скрипт
/// принимает только настоящие нажатия (isTrusted: событие, созданное
/// страницей, не годится) и подписывает сообщение меткой сеанса, которая
/// живёт в замыкании скрипта: страница её не видит. Сообщение без метки или
/// с чужим сочетанием окно отбрасывает (Read).
public static class PageKeys
{
    /// Скрипт для AddScriptToExecuteOnDocumentCreatedAsync. Он выполняется до
    /// скриптов страницы, поэтому успевает взять postMessage и setTimeout до
    /// того, как страница могла бы их подменить.
    ///
    /// Клавиша берётся по keyCode — виртуальной клавише Windows, как у сочетаний
    /// окна: при русской раскладке Ctrl+T остаётся Ctrl+T, хотя e.key там «е».
    /// AltGr приходит как Ctrl+Alt; такого сочетания в списке нет, и скрипт его
    /// пропускает.
    public static string Script(string token) => $$"""
        (() => {
          const view = window.chrome && window.chrome.webview;
          if (!view) return;
          const post = view.postMessage.bind(view);
          const later = window.setTimeout.bind(window);
          const keys = {{Json(Combo.Keys)}};
          const reserved = new Set({{Json(Names(yields: false))}});
          const yielding = new Set({{Json(Names(yields: true))}});
          window.addEventListener('keydown', e => {
            if (!e.isTrusted || e.metaKey) return;
            const key = keys[e.keyCode];
            if (!key) return;
            const name = (e.ctrlKey ? 'ctrl+' : '') + (e.altKey ? 'alt+' : '') + (e.shiftKey ? 'shift+' : '') + key;
            if (reserved.has(name)) {
              e.preventDefault();
              e.stopImmediatePropagation();
              post('{{token}}:' + name);
              return;
            }
            if (!yielding.has(name)) return;
            // Своё действие движка (Ctrl+S — «Сохранить как») гасим сразу, а
            // забрала ли сочетание страница, узнаём по её preventDefault,
            // когда событие пройдёт всю страницу.
            const prevent = e.preventDefault;
            let taken = false;
            e.preventDefault = function () { taken = true; return prevent.call(this); };
            prevent.call(e);
            later(() => { if (!taken) post('{{token}}:' + name); }, 0);
          }, true);
        })();
        """;

    /// Сочетание из сообщения страницы, если оно подписано меткой сеанса и
    /// окно его сейчас ждёт (есть в Shortcuts.Page); иначе null.
    public static string? Read(string? message, string token)
    {
        if (string.IsNullOrEmpty(token) || message is null || !message.StartsWith(token + ":", StringComparison.Ordinal))
            return null;
        var name = message[(token.Length + 1)..];
        return Shortcuts.Page().Any(page => page.Name == name) ? name : null;
    }

    static string[] Names(bool yields) => [.. Shortcuts.Page().Where(page => page.Yields == yields).Select(page => page.Name)];

    // JSON — это и JS-литерал: кавычки и обратный слеш экранируются верно. Строгий
    // кодировщик заменил бы «+» и «'» на \u-коды: верно, но нечитаемо в скрипте.
    static string Json<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
