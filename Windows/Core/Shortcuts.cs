namespace Browser;

/// Клавиша и модификаторы. Имена клавиш строчные, как в настройке и в скрипте
/// страницы. Коды — виртуальные клавиши Windows: при русской раскладке Ctrl+T
/// остаётся Ctrl+T, хотя символ там другой.
public sealed record Combo(string Key, bool Ctrl = false, bool Shift = false, bool Alt = false)
{
    /// Виртуальный код клавиши → имя.
    public static readonly IReadOnlyDictionary<int, string> Keys = Table();

    static Dictionary<int, string> Table()
    {
        var table = new Dictionary<int, string>
        {
            [8] = "backspace", [9] = "tab", [13] = "return", [27] = "escape", [32] = "space",
            [33] = "pageup", [34] = "pagedown", [35] = "end", [36] = "home",
            [37] = "left", [38] = "up", [39] = "right", [40] = "down",
            [45] = "insert", [46] = "delete",
            [186] = ";", [187] = "=", [188] = ",", [189] = "-", [190] = ".", [191] = "/",
            [192] = "`", [219] = "[", [220] = "\\", [221] = "]", [222] = "'",
        };
        for (var i = 0; i < 26; i++) table[65 + i] = ((char)('a' + i)).ToString();
        for (var i = 0; i < 10; i++) table[48 + i] = i.ToString();
        for (var i = 1; i <= 12; i++) table[111 + i] = "f" + i;
        return table;
    }

    /// Виртуальный код клавиши, обратно к Keys.
    public int Code => Keys.First(pair => pair.Value == Key).Key;

    /// Null, если клавиши нет в таблице, например сама Ctrl (17).
    public static Combo? FromCode(int code, bool ctrl, bool shift, bool alt) =>
        Keys.TryGetValue(code, out var key) ? new Combo(key, ctrl, shift, alt) : null;

    /// Для настроек и скрипта страницы. Порядок модификаторов фиксирован, и
    /// Parse принимает только его: одно сочетание — одно имя.
    public string Name => (Ctrl ? "ctrl+" : "") + (Alt ? "alt+" : "") + (Shift ? "shift+" : "") + Key;

    /// Обратное к Name. Ключа «+» не бывает, поэтому делить по «+» можно.
    public static Combo? Parse(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var parts = name.Split('+');
        var last = 0;
        var ctrl = false;
        var alt = false;
        var shift = false;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            // Ранг растёт по порядку ctrl, alt, shift; неизвестное имя даёт 0.
            // Повтор и перестановка дают ранг не больше прежнего — отказ.
            var rank = parts[i] switch { "ctrl" => 1, "alt" => 2, "shift" => 3, _ => 0 };
            if (rank <= last) return null;
            last = rank;
            ctrl |= rank == 1;
            alt |= rank == 2;
            shift |= rank == 3;
        }
        var key = parts[^1];
        return Keys.Values.Contains(key) ? new Combo(key, ctrl, shift, alt) : null;
    }

    /// Как написать в меню: Ctrl+Shift+T, Alt+Left, F5.
    public string Display
    {
        get
        {
            var key = Key switch
            {
                "tab" => "Tab", "space" => "Space", "return" => "Enter", "escape" => "Esc",
                "backspace" => "Backspace", "delete" => "Delete", "insert" => "Insert",
                "home" => "Home", "end" => "End", "pageup" => "PageUp", "pagedown" => "PageDown",
                "left" => "Left", "up" => "Up", "right" => "Right", "down" => "Down",
                _ => Key.ToUpperInvariant(),
            };
            return (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + key;
        }
    }

    /// Без Ctrl и Alt клавиша — это набор текста, сочетанием она не бывает.
    /// Функциональные клавиши сами по себе не набирают текст, поэтому годятся.
    public bool Usable => Ctrl || Alt || (Key.Length > 1 && Key[0] == 'f' && char.IsDigit(Key[1]));
}

/// Команда окна. Default — её сочетание по умолчанию, null — без сочетания.
/// Native — движок сам делает это сочетание внутри страницы (печать,
/// перезагрузка), пока оно на умолчании: страница его не получает. Yields —
/// страница может забрать сочетание себе (Ctrl+S в редакторе); false —
/// страница его не получает никогда. Also — вторая клавиша, только для показа.
public sealed record Command(string Id, string Title, Combo? Default, bool Native = false, bool Yields = true, string? Also = null);

/// Таблица команд окна и их сочетаний. Меняет только Settings › Shortcuts:
/// страница и расширения этого не могут. В настройке лежит только отличие от
/// умолчания, поэтому умолчание, которое поменяется позже, дойдёт до всех, кто
/// его не трогал.
public static class Shortcuts
{
    /// Ключ настройки, как в апстриме. Значение — список «id=имя»; «id=» — сочетания нет.
    const string Stored = "shortcuts";

    public static readonly Command[] All =
    [
        new("app.settings", "Settings…", Bind("ctrl+,")),
        new("file.newTab", "New Tab", Bind("ctrl+t"), Yields: false),
        new("file.openAddress", "Open Address…", Bind("ctrl+l")),
        new("file.closeTab", "Close Tab", Bind("ctrl+w"), Yields: false),
        new("file.print", "Print…", Bind("ctrl+p"), Native: true),
        new("view.fold", "Hide Sidebar", Bind("ctrl+s")),
        new("view.focus", "Focus Mode", Bind("ctrl+shift+f")),
        new("view.reload", "Reload Page", Bind("ctrl+r"), Native: true, Also: "F5"),
        new("view.inspector", "Developer Tools", Bind("ctrl+shift+i"), Native: true, Also: "F12"),
        new("tabs.back", "Back", Bind("alt+left"), Native: true),
        new("tabs.forward", "Forward", Bind("alt+right"), Native: true),
        new("tabs.next", "Next Tab", Bind("ctrl+tab"), Yields: false),
        new("tabs.previous", "Previous Tab", Bind("ctrl+shift+tab"), Yields: false),
        new("history.show", "Show History…", Bind("ctrl+h")),
        new("history.downloads", "Downloads…", Bind("ctrl+j"), Native: true),
    ];

    /// Умолчания пишутся строкой: ошибка в имени упадёт сразу, при первом обращении.
    static Combo Bind(string name) => Combo.Parse(name) ?? throw new ArgumentException(name);

    /// Правка текста. Эти сочетания отдаём полю ввода, а не окну.
    static readonly Combo[] Editing =
    [
        new("a", Ctrl: true), new("c", Ctrl: true), new("v", Ctrl: true), new("x", Ctrl: true),
        new("z", Ctrl: true), new("y", Ctrl: true), new("z", Ctrl: true, Shift: true), new("insert", Ctrl: true),
    ];

    public static Command? Named(string id) => All.FirstOrDefault(command => command.Id == id);

    /// Сочетание команды сейчас: своё (в том числе «нет») или умолчание. Настройку
    /// не кэшируем: её держит Settings, и проверки и окно видят одно и то же.
    public static Combo? Key(string id)
    {
        var command = Named(id);
        if (command is null) return null;
        return Overrides().TryGetValue(id, out var own) ? own : command.Default;
    }

    public static bool IsChanged(string id) => Named(id) is { } command && Key(id) != command.Default;

    public static bool AnyChanged => All.Any(command => IsChanged(command.Id));

    /// Другая команда, у которой сейчас это сочетание.
    public static Command? Owner(Combo combo, string except) =>
        All.FirstOrDefault(command => command.Id != except && Key(command.Id) == combo);

    /// Сочетание команде. У прежнего владельца оно снимается: одно сочетание —
    /// одна команда, как в меню. Сочетание, которое не пройдёт Refusal, не
    /// пишем: Overrides его при чтении отбросит, и команда тихо вернулась бы
    /// к умолчанию.
    public static void Assign(Combo combo, string id)
    {
        if (Refusal(combo) is not null || Named(id) is null) return;
        var changes = Overrides();
        if (Owner(combo, id) is { } other) changes[other.Id] = null;
        changes[id] = combo;
        Save(changes);
    }

    public static void Clear(string id) => Put(id, null);

    /// Умолчание назад. Если его занял другой, тот теряет его, как при Assign.
    public static void Reset(string id)
    {
        if (Named(id)?.Default is { } fallback) Assign(fallback, id);
        else Clear(id);
    }

    public static void ResetAll() => Settings.Remove(Stored);

    /// Сочетания, которые не наши: их отдаём системе, полю ввода или странице.
    /// Список из Shortcuts.swift апстрима, переведённый на Windows.
    public static bool Reserved(Combo c) =>
        c.Key == "escape"
        || (c.Key == "tab" && !c.Ctrl)
        || c.Key is "f5" or "f10" or "f12"
        || (c.Ctrl && c.Alt)
        || (c.Alt && c.Key is "f4" or "space")
        || (c.Ctrl && c.Key is "left" or "right" or "up" or "down" or "home" or "end" or "backspace" or "delete")
        || Editing.Contains(c);

    /// Почему сочетание нельзя, или null, если можно. Сначала «не наше»: Tab и
    /// Esc без модификаторов — не про «добавьте Ctrl или Alt».
    public static string? Refusal(Combo c) =>
        Reserved(c) ? "Can’t be changed" : !c.Usable ? "Add Ctrl or Alt" : null;

    /// Сочетания для скрипта страницы: имя и может ли страница его забрать.
    /// Native-команда на своём умолчании сюда не входит: её делает движок.
    public static IEnumerable<(string Name, bool Yields)> Page()
    {
        foreach (var command in All)
        {
            if (Key(command.Id) is not { } combo) continue;
            if (command.Native && combo == command.Default) continue;
            yield return (combo.Name, command.Yields);
        }
    }

    static void Put(string id, Combo? combo)
    {
        var changes = Overrides();
        changes[id] = combo;
        Save(changes);
    }

    /// Переопределения из настройки. Непонятные записи пропускаются: файл,
    /// правленный руками, не должен ронять окно.
    static Dictionary<string, Combo?> Overrides()
    {
        var changes = new Dictionary<string, Combo?>();
        foreach (var entry in Settings.List(Stored))
        {
            var at = entry.IndexOf('=');
            if (at < 1) continue;
            var id = entry[..at];
            var text = entry[(at + 1)..];
            if (Named(id) is null) continue;
            if (text.Length == 0) changes[id] = null;
            else if (Combo.Parse(text) is { } combo && Refusal(combo) is null) changes[id] = combo;
        }
        return changes;
    }

    /// Пишется только отличие от умолчания, в порядке All. Пусто — настройку
    /// удаляем, как это делает апстрим.
    static void Save(Dictionary<string, Combo?> changes)
    {
        var lines = new List<string>();
        foreach (var command in All)
            if (changes.TryGetValue(command.Id, out var combo) && combo != command.Default)
                lines.Add(command.Id + "=" + (combo?.Name ?? ""));
        if (lines.Count > 0) Settings.Set(Stored, lines);
        else Settings.Remove(Stored);
    }
}
