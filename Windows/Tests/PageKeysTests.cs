namespace Browser.Tests;

static class PageKeysTests
{
    public static void Run()
    {
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
        Check.Expect(PageKeys.Reserved.Concat(PageKeys.Yielding).All(name => script.Contains($"'{name}'")), "все сочетания есть в скрипте");
        Check.Expect(script.Contains("e.isTrusted"), "скрипт берёт только настоящие нажатия");
    }
}
