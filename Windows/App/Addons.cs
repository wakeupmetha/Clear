using Microsoft.Web.WebView2.Core;

namespace Browser;

/// Расширения, которые человек ставит сам, из распакованной папки или из
/// Chrome Web Store: копия в хранилище (Extensions в Core) и её — движку. uBlock Origin ставится иначе,
/// из установки (UBlock).
///
/// Всё по id и на профиле, который дали сейчас: профиль бывает от невидимого
/// движка на время одного дела (Tabs.WithProfile), и расширение, полученное от
/// него, после дела уже не ответит.
// ponytail: та же папка второй раз встаёт вторым расширением, а не обновляет
// первое. Мешает — искать запись с тем же Source и сначала убирать её (Reload апстрима).
static class Addons
{
    /// Расширение поставили или убрали: страница настроек перечитывает список,
    /// откуда бы это ни пришло (её кнопка, тест-канал).
    public static event Action? Changed;

    /// Расширение включили или выключили, или движок проснулся и список стал
    /// известен: меняются кнопки в полосе (Actions). Строки настроек об этом
    /// не перечитываются — их переключатель и так знает.
    public static event Action? Switched;

    public static void Switch() => Switched?.Invoke();

    static readonly HttpClient Web = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// Из распакованной папки человека.
    public static Task<(string Id, string Name)> Install(CoreWebView2Profile profile, string source) =>
        Admit(profile, Extensions.Stage(source), source);

    /// Из Chrome Web Store по ссылке на страницу расширения или по id: .crx с
    /// открытого адреса обновлений, подпись сверена с id (Crx).
    // ponytail: id у движка свой (по пути папки), не id магазина. Мешает тем,
    // кто ждёт свой id (хосты native messaging), — вписать в manifest.json
    // "key" из проверенного заголовка.
    public static async Task<(string Id, string Name)> InstallFromStore(CoreWebView2Profile profile, string text)
    {
        var id = Crx.Id(text) ?? throw new InvalidDataException("That isn't a Chrome Web Store link or extension id");
        var source = StorePage(id);
        if (Extensions.All().Any(record => record.Source == source)) throw new InvalidDataException("That extension is already added");
        // Представляемся той версией Chromium, что стоит на самом деле.
        var chrome = CoreWebView2Environment.GetAvailableBrowserVersionString().Split(' ')[0];
        using var answer = await Web.GetAsync(Crx.DownloadUrl(id, chrome));
        if (!answer.IsSuccessStatusCode) throw new InvalidDataException($"The Chrome Web Store answered {(int)answer.StatusCode}");
        var crx = await answer.Content.ReadAsByteArrayAsync();
        if (crx.Length == 0) throw new InvalidDataException("The Chrome Web Store has nothing for that id: it may have been taken down");
        return await Admit(profile, Extensions.Unpack(Crx.VerifiedZip(crx, id), id), source);
    }

    /// Готовую копию в Extensions — движку, и запись о ней.
    static async Task<(string Id, string Name)> Admit(CoreWebView2Profile profile, string copy, string source)
    {
        try
        {
            var added = await profile.AddBrowserExtensionAsync(copy);
            var manifest = Extensions.Manifest(copy);
            Extensions.Keep(new Installed(added.Id, added.Name, manifest?.Version ?? "", Path.GetFileName(copy), manifest?.Options, source));
            Changed?.Invoke();
            return (added.Id, added.Name);
        }
        catch
        {
            // Движок не принял: копия никому не нужна.
            try { Directory.Delete(copy, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// То, что человек поставил сам: без uBlock (у него своя строка) и без
    /// встроенных в движок (буфер обмена, PDF), о которых installed.json не знает.
    public static async Task<List<(Installed Record, string Name, bool Enabled)>> Mine(CoreWebView2Profile profile)
    {
        var records = Extensions.All();
        var mine = new List<(Installed, string, bool)>();
        foreach (var found in await profile.GetBrowserExtensionsAsync())
            if (records.FirstOrDefault(record => record.Id == found.Id) is { } record) mine.Add((record, found.Name, found.IsEnabled));
        return mine;
    }

    public static async Task Enable(CoreWebView2Profile profile, string id, bool on)
    {
        if (await Find(profile, id) is { } found && found.IsEnabled != on) await found.EnableAsync(on);
        Switch();
    }

    public static async Task Remove(CoreWebView2Profile profile, string id)
    {
        if (await Find(profile, id) is { } found) await found.RemoveAsync();
        Extensions.Forget(id);
        Changed?.Invoke();
    }

    /// Откуда поставлено из магазина (Installed.Source): страница расширения.
    public static string StorePage(string id) => $"https://chromewebstore.google.com/detail/{id}";

    public static Uri? Options(Installed record) =>
        record.Options is { } page ? new Uri($"chrome-extension://{record.Id}/{page.TrimStart('/')}") : null;

    static async Task<CoreWebView2BrowserExtension?> Find(CoreWebView2Profile profile, string id) =>
        (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(found => found.Id == id);
}
