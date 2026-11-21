namespace Browser.Tests;

static class PageKeysTests
{
    public static void Run()
    {
        Check.World("shortcuts");
        Settings.Remove("shortcuts");
        const string token = "0123456789abcdef0123456789abcdef";
        Check.Expect(PageKeys.Read(token + ":ctrl+t", token) == "ctrl+t", "подписанное сочетание из списка принимается");
        Check.Expect(PageKeys.Read(token + ":ctrl+shift+tab", token) == "ctrl+shift+tab", "Ctrl+Shift+Tab принимается");
        Check.Expect(PageKeys.Read(token + ":ctrl+,", token) == "ctrl+,", "Ctrl+запятая принимается");
        Check.Expect(PageKeys.Read(token + ":ctrl+shift+f", token) == "ctrl+shift+f" && PageKeys.Read(token + ":ctrl+shift+s", token) is null,
            "Ctrl+Shift+F (режим фокуса) принимается, Ctrl+Shift+S — нет");
        Check.Expect(PageKeys.Read("ffffffffffffffffffffffffffffffff:ctrl+w", token) is null, "чужая метка — не сочетание");
        Check.Expect(PageKeys.Read("ctrl+w", token) is null, "без метки — не сочетание");
        Check.Expect(PageKeys.Read(token + ":ctrl+q", token) is null, "сочетание не из списка отбрасывается");
        Check.Expect(PageKeys.Read(token + ":ctrl+t:x", token) is null, "хвост после сочетания отбрасывается");
        Check.Expect(PageKeys.Read(null, token) is null && PageKeys.Read(":ctrl+t", "") is null, "пустое сообщение и пустая метка — ничего");

        var script = PageKeys.Script(token);
        Check.Expect(script.Contains($"'{token}:'"), "метка сеанса стоит в скрипте");
        Check.Expect(Shortcuts.Page().All(page => script.Contains($"\"{page.Name}\"")), "все сочетания страницы есть в скрипте");
        Check.Expect(script.Contains("e.isTrusted"), "скрипт берёт только настоящие нажатия");

        // Нажатие по ссылке: за спиной или перед глазами, только с меткой сеанса.
        Check.Expect(PageKeys.Clicked(token + ":click:behind", token) == false && PageKeys.Clicked(token + ":click:front", token) == true,
            "средняя кнопка — за спиной, с Shift — перед глазами");
        Check.Expect(PageKeys.Clicked("ffffffffffffffffffffffffffffffff:click:behind", token) is null && PageKeys.Clicked("click:behind", token) is null
            && PageKeys.Clicked(token + ":click:aside", token) is null && PageKeys.Clicked(token + ":click:behind", "") is null && PageKeys.Read(token + ":click:behind", token) is null,
            "чужая метка, без метки, чужое слово, пустая метка — не нажатие; и это не сочетание");
        Check.Expect(script.Contains($"'{token}:click:'"), "скрипт сообщает о нажатии по ссылке с меткой");

        // Переназначенное сочетание: скрипт и разбор его ловят, старое — нет.
        Shortcuts.Assign(Combo.Parse("f2")!, "file.newTab");
        script = PageKeys.Script(token);
        Check.Expect(PageKeys.Read(token + ":f2", token) == "f2" && PageKeys.Read(token + ":ctrl+t", token) is null && script.Contains("\"f2\""),
            "новое сочетание принимается и скриптом, старое отбрасывается");
        Shortcuts.ResetAll();
    }
}
