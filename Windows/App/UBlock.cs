using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace Browser;

/// uBlock Origin, который лежит в установке (решение владельца 2026-11-20:
/// полный uBlock Origin внутри установщика). Встаёт в профиль движка один раз
/// и дальше движок поднимает его сам; переключатель блокировщика включает и
/// выключает его. Пока uBlock не нашёлся или не встал, работает свой список
/// (Shield). Откуда он взят и под какой лицензией — Extensions\uBlock Origin.txt.
static class UBlock
{
    /// Распакованное расширение рядом с aerio.exe. Движок его не копирует:
    /// папку трогать нельзя, пока он стоит.
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Extensions", "uBlock0.chromium");

    static CoreWebView2BrowserExtension? installed;

    /// Id у движка, когда он найден.
    public static string? Id => installed?.Id;

    /// Адрес его панели (списки фильтров, доверенные сайты), когда он найден.
    public static Uri? Dashboard => installed is null ? null : new Uri($"chrome-extension://{installed.Id}/dashboard.html");

    /// Найти или поставить. Один раз за сеанс, до первой загрузки: иначе первая
    /// страница пройдёт без него.
    // ponytail: только что поставленный uBlock несколько секунд разбирает свои
    // списки, и первая страница самого первого запуска проходит без него.
    // Мешает — на это время оставить включённым свой список.
    // ponytail: узнаём его по имени из манифеста. Поставит человек свой «uBlock
    // Origin» (когда будет страница расширений) — будет считаться этим.
    public static async Task Ensure(CoreWebView2Profile profile)
    {
        try
        {
            installed = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(found => found.Name == "uBlock Origin");
            if (installed is null && Directory.Exists(Folder)) installed = await profile.AddBrowserExtensionAsync(Folder);
        }
        catch (Exception e) when (e is COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            // Движок отказался: блокирует свой список.
            installed = null;
        }
        Shield.Delegated = installed is not null;
        await Follow();
        // Первый движок проснулся: теперь известно, у каких расширений кнопки.
        Addons.Switch();
    }

    /// uBlock включён, когда включён блокировщик.
    public static async Task Follow()
    {
        if (installed is null || installed.IsEnabled == Shield.Enabled) return;
        try { await installed.EnableAsync(Shield.Enabled); }
        catch (COMException) { }
        Addons.Switch();
    }
}
