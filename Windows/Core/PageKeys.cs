namespace Browser;

/// Сочетания окна, нажатые, пока фокус с клавиатуры в странице. Нажатия в
/// странице забирает движок, и до сочетаний окна (KeyboardAccelerators) они
/// не доходят. Поэтому скрипт в каждой странице ловит эти сочетания и
/// сообщает о них окну.
///
/// Сайт не должен уметь сам закрыть вкладку или открыть новые. Скрипт
/// принимает только настоящие нажатия (isTrusted: событие, созданное
/// страницей, не годится) и подписывает сообщение меткой сеанса, которая
/// живёт в замыкании скрипта: страница её не видит. Сообщение без метки или
/// с чужим сочетанием окно отбрасывает (Read).
public static class PageKeys
{
    /// Как в Chrome: эти сочетания страница не получает, окно берёт их сразу.
    public static readonly string[] Reserved = ["ctrl+t", "ctrl+w", "ctrl+tab", "ctrl+shift+tab"];

    /// Эти страница может забрать себе (preventDefault), и тогда окно их не
    /// трогает: Ctrl+S в редакторе документов сохраняет документ.
    public static readonly string[] Yielding = ["ctrl+l", "ctrl+s", "ctrl+h", "ctrl+,", "ctrl+shift+f"];

    /// Скрипт для AddScriptToExecuteOnDocumentCreatedAsync. Он выполняется до
    /// скриптов страницы, поэтому успевает взять postMessage и setTimeout до
    /// того, как страница могла бы их подменить.
    ///
    /// Клавиша берётся по keyCode — виртуальной клавише Windows, как у сочетаний
    /// окна: при русской раскладке Ctrl+T остаётся Ctrl+T, хотя e.key там «е».
    /// AltGr приходит как Ctrl+Alt и сочетанием не считается.
    public static string Script(string token) => $$"""
        (() => {
          const view = window.chrome && window.chrome.webview;
          if (!view) return;
          const post = view.postMessage.bind(view);
          const later = window.setTimeout.bind(window);
          const letters = { 84: 't', 87: 'w', 76: 'l', 83: 's', 72: 'h', 188: ',', 70: 'f' };
          const reserved = new Set({{List(Reserved)}});
          const yielding = new Set({{List(Yielding)}});
          window.addEventListener('keydown', e => {
            if (!e.isTrusted || !e.ctrlKey || e.altKey || e.metaKey) return;
            const name = e.keyCode === 9 ? (e.shiftKey ? 'ctrl+shift+tab' : 'ctrl+tab')
              : letters[e.keyCode] && 'ctrl+' + (e.shiftKey ? 'shift+' : '') + letters[e.keyCode];
            if (!name) return;
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
    /// входит в список; иначе null.
    public static string? Read(string? message, string token)
    {
        if (string.IsNullOrEmpty(token) || message is null || !message.StartsWith(token + ":", StringComparison.Ordinal))
            return null;
        var name = message[(token.Length + 1)..];
        return Reserved.Contains(name) || Yielding.Contains(name) ? name : null;
    }

    static string List(string[] names) => "[" + string.Join(", ", names.Select(name => $"'{name}'")) + "]";
}
