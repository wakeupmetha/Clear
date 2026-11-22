namespace Browser.Tests;

static class ShortcutsTests
{
    public static void Run()
    {
        Check.World("shortcuts");
        Settings.Remove("shortcuts");
        Names();
        Choices();
        Storage();
        Pages();
    }

    static void Names()
    {
        foreach (var name in (string[])["ctrl+shift+tab", "ctrl+,", "alt+left", "f2", "ctrl+\\"])
            Check.Expect(Combo.Parse(name) is { } combo && combo.Name == name, $"«{name}» читается и пишется обратно");
        Check.Expect(((string[])["shift+ctrl+t", "ctrl+ctrl+t", "ctrl+foo", ""]).All(name => Combo.Parse(name) is null),
            "Parse отвергает порядок модификаторов, повтор, чужую клавишу и пустое имя");
        Check.Expect(new Combo("t", Ctrl: true, Shift: true).Display == "Ctrl+Shift+T" && new Combo("left", Alt: true).Display == "Alt+Left"
            && new Combo("f5").Display == "F5" && new Combo(",", Ctrl: true).Display == "Ctrl+,", "в меню: Ctrl+Shift+T, Alt+Left, F5, Ctrl+,");
        Check.Expect(Combo.FromCode(84, true, false, false) == new Combo("t", Ctrl: true) && new Combo("t").Code == 84, "код 84 — это T");
        Check.Expect(Combo.FromCode(17, true, false, false) is null, "сама клавиша Ctrl сочетанием не бывает");
    }

    static string? Refuse(string name) => Shortcuts.Refusal(Combo.Parse(name)!);

    static void Choices()
    {
        Check.Expect(!Combo.Parse("t")!.Usable && Refuse("t") == "Add Ctrl or Alt", "буква без Ctrl и Alt — набор текста");
        Check.Expect(Combo.Parse("f2")!.Usable && Refuse("f2") is null, "F2 годится");
        Check.Expect(Refuse("ctrl+c") == "Can’t be changed" && Refuse("ctrl+alt+t") == "Can’t be changed" && Refuse("shift+tab") == "Can’t be changed"
            && Refuse("f5") == "Can’t be changed" && Refuse("ctrl+left") == "Can’t be changed", "системные и правка текста — не отдаём");
        Check.Expect(Refuse("ctrl+tab") is null && Refuse("alt+left") is null, "Ctrl+Tab и Alt+Left годятся");
        Check.Expect(Refuse("ctrl+1") == "Can’t be changed" && Refuse("ctrl+9") == "Can’t be changed" && Refuse("ctrl+0") is null && Refuse("ctrl+shift+1") is null,
            "Ctrl+1–Ctrl+9 — вкладки по номеру, не отдаём; Ctrl+0 и Ctrl+Shift+1 годятся");
        Check.Expect(Refuse("ctrl+delete") == "Can’t be changed" && Refuse("ctrl+shift+delete") is null,
            "Ctrl+Delete — стереть слово, не отдаём; Ctrl+Shift+Delete (очистка данных) годится");
        Check.Expect(Shortcuts.All.All(command => command.Default is { } fallback && Shortcuts.Refusal(fallback) is null),
            "у всех умолчаний нет отказа");
        Check.Expect(Shortcuts.All.Select(command => command.Default).Distinct().Count() == Shortcuts.All.Length, "ни одно умолчание не повторяется");
    }

    static void Storage()
    {
        Shortcuts.Assign(new Combo("t", Ctrl: true), "history.show");
        Check.Expect(Shortcuts.Key("history.show") == new Combo("t", Ctrl: true) && Shortcuts.Key("file.newTab") is null,
            "сочетание перешло к новой команде, у прежней его нет");
        Check.Expect(Shortcuts.IsChanged("history.show") && Shortcuts.IsChanged("file.newTab"), "обе команды отмечены как изменённые");
        Shortcuts.Reset("file.newTab");
        Check.Expect(Shortcuts.Key("file.newTab") == new Combo("t", Ctrl: true) && Shortcuts.Key("history.show") is null,
            "умолчание вернулось и снято с того, кто его занял");
        Shortcuts.ResetAll();
        Check.Expect(!Shortcuts.AnyChanged && Settings.List("shortcuts").Length == 0, "сброс всего — настройки нет");

        Shortcuts.Assign(Combo.Parse("f2")!, "file.print");
        Check.Expect(Settings.List("shortcuts") is ["file.print=f2"], "в настройке только отличие от умолчания");
        Shortcuts.Assign(Combo.Parse("ctrl+p")!, "file.print");
        Check.Expect(Settings.List("shortcuts").Length == 0, "вернули умолчание — запись пропала");

        Settings.Set("shortcuts", ["nope=ctrl+q", "file.print=ctrl+c", "view.fold=garbage", "tabs.next="]);
        Check.Expect(Shortcuts.Key("tabs.next") is null && Shortcuts.IsChanged("tabs.next"), "пустое «id=» — сочетания нет");
        Check.Expect(Shortcuts.All.Where(command => command.Id != "tabs.next").All(command => !Shortcuts.IsChanged(command.Id)),
            "чужие и непонятные записи пропущены: ctrl+c, ctrl+q и garbage не действуют");
        Shortcuts.ResetAll();
    }

    static void Pages()
    {
        (string Name, bool Yields)[] eighteen =
        [
            ("ctrl+,", true), ("ctrl+t", false), ("ctrl+shift+n", false), ("ctrl+shift+t", false), ("ctrl+l", true), ("ctrl+w", false),
            ("ctrl+f", true), ("ctrl+s", true),
            ("ctrl+shift+f", true), ("f11", false), ("ctrl+tab", false), ("ctrl+shift+tab", false), ("ctrl+d", true), ("ctrl+shift+c", true),
            ("ctrl+shift+v", true), ("ctrl+shift+b", true), ("ctrl+h", true), ("ctrl+shift+delete", false),
        ];
        var numbers = Enumerable.Range(1, 9).Select(n => ($"ctrl+{n}", false));
        Check.Expect(Shortcuts.Page().SequenceEqual(eighteen.Concat(numbers)),
            "при умолчаниях странице отдаются ровно восемнадцать сочетаний команд с верным Yields и Ctrl+1–Ctrl+9, которые страница не получает");

        Shortcuts.Assign(Combo.Parse("f2")!, "file.print");
        var page = Shortcuts.Page().ToArray();
        Check.Expect(page.Any(item => item.Name == "f2") && !page.Any(item => item.Name is "ctrl+r" or "ctrl+shift+r" or "ctrl+j" or "alt+left" or "ctrl+shift+i"),
            "новое сочетание попадает в страницу, а Native-команды на умолчании — нет");

        Shortcuts.Clear("file.newTab");
        Check.Expect(!Shortcuts.Page().Any(item => item.Name == "ctrl+t"), "снятое сочетание из страницы уходит");
        Shortcuts.ResetAll();
    }
}
