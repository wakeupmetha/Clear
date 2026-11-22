using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;

namespace Browser;

/// Карточка сайта живой вкладки, из кнопки слева от адресной строки: блокировщик
/// на этом сайте, разрешения сайта, его куки и данные. SiteCard из
/// Sources/Search/SiteCard.swift — без строк, которым здесь нет своего дела или
/// которые не перенесены (связь, печать, масштаб, звук). Стереть данные одного
/// сайта — наше, в апстриме такого нет: просьба владельца 2026-11-20 («не
/// понятно, где чистка куки отдельно по сайтам»).
sealed class SiteCard
{
    readonly Tabs tabs;
    // Открыть окно расширения из полосы (Actions.Press): у uBlock там свой
    // переключатель «на этом сайте».
    readonly Action<string> press;
    bool opening;

    // Разрешения и слова — как в апстриме (CaptureChoices.swift). «Ask» — выбора
    // нет, и движок спрашивает сам; «Allow» и «Refuse» движок помнит в профиле.
    static readonly (CoreWebView2PermissionKind Kind, string Title)[] Kinds =
    [
        (CoreWebView2PermissionKind.Camera, "Camera"), (CoreWebView2PermissionKind.Microphone, "Microphone"),
        (CoreWebView2PermissionKind.Geolocation, "Location"), (CoreWebView2PermissionKind.Notifications, "Notifications"),
    ];
    static readonly (string Value, string Title)[] Choices = [("ask", "Ask"), ("allow", "Allow"), ("refuse", "Refuse")];

    public SiteCard(Tabs tabs, Action<string> press)
    {
        this.tabs = tabs;
        this.press = press;
    }

    public async void Show(FrameworkElement anchor)
    {
        if (opening || tabs.Live is not { IsOwn: false, Page: { Scheme: "http" or "https" } page } tab) return;
        opening = true;
        // Разрешения движок хранит по источнику — схема, хост и порт, — как и апстрим.
        var origin = page.GetLeftPart(UriPartial.Authority);
        var host = Address.WithoutWWW(page.Host);
        var card = new StackPanel { Width = 330, Padding = new Thickness(0, 4, 0, 6) };
        var flyout = new Flyout { Content = card, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft, FlyoutPresenterStyle = Actions.Plain };
        card.Children.Add(new TextBlock
        {
            Text = host, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Palette.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(14, 10, 14, 8),
        });

        // С uBlock доверенные сайты — его: выключить на сайте можно только в его
        // окне, туда и ведёт строка.
        // ponytail: свой переключатель для uBlock — через его сообщения
        // (toggleNetFiltering из окна uBlock), если окна окажется мало.
        if (Shield.Enabled && Shield.Delegated && UBlock.Id is { } ublock)
        {
            card.Children.Add(Row("Block Ads and Trackers", "uBlock Origin blocks here. Turn it off for this site in its window",
                Ui.Pill("Open uBlock Origin", Ui.Word("Open"), () =>
                {
                    flyout.Hide();
                    press(ublock);
                })));
        }
        // Свой блокировщик (uBlock не поставлен).
        else if (Shield.Enabled && !Shield.Delegated)
        {
            var ready = false;
            var toggle = Ui.Switch("Block Ads and Trackers", on =>
            {
                if (!ready) return;
                if (on) Shield.Resume(host);
                else Shield.Pause(host, true);
                // Как в апстриме: блокировщик меняет то, что страница загрузила.
                tab.View?.CoreWebView2?.Reload();
            });
            toggle.IsChecked = !Shield.PausedOn(host);
            ready = true;
            card.Children.Add(Row("Block Ads and Trackers", "Turn off here if the site breaks; the page reloads", toggle));
        }

        card.Children.Add(Rule());
        card.Children.Add(new TextBlock { Text = "Site Permissions", FontSize = 11.5, Foreground = Palette.Muted, Margin = new Thickness(14, 10, 14, 2) });
        var shows = new List<(CoreWebView2PermissionKind Kind, Action<string> Show)>();
        foreach (var (kind, title) in Kinds)
        {
            var (choice, show) = Ui.Segmented(title, Choices, value => _ = Permit(kind, value));
            shows.Add((kind, show));
            card.Children.Add(Row(title, null, choice));
        }

        card.Children.Add(Rule());
        var note = new TextBlock { Text = "Signs you out of this site", FontSize = 11.5, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap };
        var clear = Ui.Pill("Clear site data", Ui.Word("Clear"));
        clear.Click += async (_, _) =>
        {
            note.Text = "Clearing…";
            note.Foreground = Palette.Muted;
            try
            {
                await Wipe(tab, page, origin);
                note.Text = "Cleared. The page reloads";
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
            {
                note.Text = $"Couldn't clear: {e.Message}";
                note.Foreground = Palette.Danger;
            }
        };
        card.Children.Add(Row("Cookies and site data", note, clear));

        await Read();
        opening = false;
        // Пока читали, вкладку могли сменить: карточка — о той, что была.
        if (tabs.Live != tab) return;
        flyout.ShowAt(anchor);

        // Что движок помнит про этот источник; ничего — «Ask».
        async Task Read()
        {
            IReadOnlyList<CoreWebView2PermissionSetting> saved = [];
            try { await tabs.WithProfile(async profile => saved = await profile.GetNonDefaultPermissionSettingsAsync()); }
            catch (COMException) { }
            foreach (var (kind, show) in shows)
            {
                var state = saved.FirstOrDefault(setting => setting.PermissionKind == kind && Same(setting.PermissionOrigin, origin))?.PermissionState;
                show(state switch { CoreWebView2PermissionState.Allow => "allow", CoreWebView2PermissionState.Deny => "refuse", _ => "ask" });
            }
        }

        async Task Permit(CoreWebView2PermissionKind kind, string value)
        {
            var state = value switch { "allow" => CoreWebView2PermissionState.Allow, "refuse" => CoreWebView2PermissionState.Deny, _ => CoreWebView2PermissionState.Default };
            try { await tabs.WithProfile(profile => profile.SetPermissionStateAsync(kind, origin, state).AsTask()); }
            // Не записалось — карточка показывает то, что движок помнит на самом деле.
            catch (Exception e) when (e is COMException or ArgumentException) { await Read(); }
        }
    }

    /// Куки и хранилища одного сайта, затем страница перезагружается.
    static async Task Wipe(Tab tab, Uri page, string origin)
    {
        if (tab.View?.CoreWebView2 is not { } core) throw new InvalidOperationException("The page isn't loaded");
        // Куки, которые уходят на эту страницу: и самого хоста, и сайта выше
        // (.example.com) — по ним сайт и узнаёт человека.
        var jar = core.CookieManager;
        foreach (var cookie in await jar.GetCookiesAsync(page.AbsoluteUri)) jar.DeleteCookie(cookie);
        // Хранилища источника: куки на других путях, localStorage, IndexedDB,
        // кэш и service worker. У WebView2 стирание только всего профиля —
        // берём отладочный протокол.
        await core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin",
            new JsonObject { ["origin"] = origin, ["storageTypes"] = "all" }.ToJsonString());
        core.Reload();
    }

    /// Тот же источник: движок пишет его по-своему (со слэшем, с портом).
    static bool Same(string saved, string origin) =>
        Uri.TryCreate(saved, UriKind.Absolute, out var url)
        && string.Equals(url.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase);

    static Border Rule() => new() { Height = 1, Background = Palette.Hairline, Margin = new Thickness(0, 6, 0, 0) };

    /// Строка карточки: имя (и пояснение под ним) слева, управление справа.
    static Grid Row(string title, object? detail, FrameworkElement control)
    {
        var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, FontSize = 12.5, Foreground = Palette.Ink });
        if (detail is TextBlock block) words.Children.Add(block);
        else if (detail is string text)
            words.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap });
        var line = new Grid { Padding = new Thickness(14, 7, 14, 7), ColumnSpacing = 14 };
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        line.Children.Add(words);
        line.Children.Add(control);
        return line;
    }
}
