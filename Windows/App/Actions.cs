using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace Browser;

/// Кнопки расширений в правом конце полосы окна и их всплывающие окна
/// (ExtensionButtons в Sources/Search/Extensions.swift, ExtensionPopup.swift).
/// У WebView2 нет ни кнопок, ни окон расширений: кнопку рисуем по манифесту,
/// окно — Flyout со своим движком в общей среде, на странице default_popup.
///
/// Отличия от апстрима: кнопка у каждого включённого расширения с action, без
/// закрепления и без списка под пазлом. Значок — из манифеста, а не тот, что
/// расширение ставит себе само (chrome.action.setIcon), и без счётчика
/// (setBadgeText): ни того, ни другого WebView2 наружу не отдаёт.
sealed class Actions
{
    public readonly StackPanel View = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    readonly Tabs tabs;
    readonly List<(string Id, string Name, Button Door, Knob Knob)> buttons = [];
    // Окно одно на всё: новое закрывает прежнее, как в апстриме.
    Flyout? open;
    string? openId;
    WebView2? web;

    public Actions(Tabs tabs)
    {
        this.tabs = tabs;
        // Поставили, убрали, включили, выключили; движок проснулся (UBlock.Ensure).
        Addons.Changed += Refresh;
        Addons.Switched += Refresh;
    }

    /// Кнопки в полосе, по порядку (тест-канал).
    public IEnumerable<(string Id, string Name)> All => buttons.Select(button => (button.Id, button.Name));

    /// Движок открытого окна и его размер (тест-канал).
    public (CoreWebView2? Core, double Width, double Height)? Popup =>
        open is null || web is null ? null : (web.CoreWebView2, web.Width, web.Height);

    public async void Refresh()
    {
        // Пока ни один движок не проснулся, списка нет; проснётся — UBlock.Ensure
        // скажет об этом через Addons.Switched.
        if (tabs.Profile is not { } profile) return;
        IReadOnlyList<CoreWebView2BrowserExtension> found;
        try { found = await profile.GetBrowserExtensionsAsync(); }
        catch (COMException) { return; }
        var records = Extensions.All();
        View.Children.Clear();
        buttons.Clear();
        foreach (var extension in found.Where(extension => extension.IsEnabled))
        {
            // Папка нужна ради манифеста: у встроенных расширений движка её нет.
            var folder = extension.Id == UBlock.Id ? UBlock.Folder
                : records.FirstOrDefault(record => record.Id == extension.Id) is { } record ? Path.Combine(Extensions.Root, record.Folder) : null;
            // Значок рисуется в 15 точек; 32 пикселя хватает до 200 %.
            if (folder is null || Extensions.Button(folder, 32) is not { } knob) continue;
            var name = knob.Title is { } title && !title.StartsWith("__MSG_", StringComparison.Ordinal) ? title : extension.Name;
            var door = Door(name, folder, knob.Icon);
            var id = extension.Id;
            door.Click += (_, _) => Press(id);
            buttons.Add((id, name, door, knob));
            View.Children.Add(door);
        }
    }

    /// Кнопка как кнопки полосы слева: плитка 26 точек, плашка под указателем.
    /// Без значка — первая буква имени, а не пазл.
    static Button Door(string name, string folder, string? icon)
    {
        var letter = new Border
        {
            Width = 15, Height = 15, CornerRadius = new CornerRadius(4), Background = Palette.Wash,
            Child = new TextBlock
            {
                Text = name[..1].ToUpperInvariant(), FontSize = 9.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Palette.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var plate = new Border
        {
            Width = Metrics.Door, Height = Metrics.Door, CornerRadius = new CornerRadius(Metrics.DoorRadius),
            Background = Palette.Clear, Child = letter,
        };
        if (icon is not null && File.Exists(Path.Combine(folder, icon.TrimStart('/'))))
        {
            var picture = new Image { Width = 15, Height = 15, Source = new BitmapImage(new Uri(Path.Combine(folder, icon.TrimStart('/')))) };
            picture.ImageFailed += (_, _) => plate.Child = letter;
            plate.Child = picture;
        }
        var door = Ui.Bare(new Button { CornerRadius = new CornerRadius(Metrics.DoorRadius), AllowFocusOnInteraction = false }, name, plate);
        door.PointerEntered += (_, _) => plate.Background = Palette.SideHover;
        door.PointerExited += (_, _) => plate.Background = Palette.Clear;
        return door;
    }

    /// Нажатие на кнопку: окно расширения под ней. Нажатие на кнопку открытого
    /// окна его закрывает.
    // ponytail: у кнопки без default_popup нажатие ничего не делает: событие
    // chrome.action.onClicked расширению из WebView2 не послать. Понадобится —
    // искать обход через отладочный протокол движка.
    public void Press(string id)
    {
        var same = openId == id;
        open?.Hide();
        if (same || buttons.FirstOrDefault(button => button.Id == id) is not { Knob.Popup: { } page } button) return;
        // Невидим, пока не измерен: иначе окно мигнёт размером по умолчанию.
        // Tab на нём не останавливается, пока страница не спросила, где она (см. Load).
        var view = new WebView2 { Width = 360, Height = 240, Opacity = 0, IsTabStop = false };
        var flyout = new Flyout { Content = view, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight, FlyoutPresenterStyle = Plain };
        (open, openId, web) = (flyout, id, view);
        flyout.Closed += (_, _) =>
        {
            if (open == flyout) (open, openId, web) = (null, null, null);
            if (view.CoreWebView2 is not null) view.Close();
        };
        flyout.ShowAt(button.Door);
        Load(flyout, view, $"chrome-extension://{id}/{page.TrimStart('/')}");
    }

    async void Load(Flyout flyout, WebView2 view, string page)
    {
        try
        {
            // Для расширения каждый WebView2 — своё окно с одной вкладкой, и окно
            // само для себя «текущее»: chrome.tabs.query({active, currentWindow})
            // нашло бы само окно, а не страницу (проверено спайком). Номер окна
            // живой вкладки у отладочного протокола тот же, что у расширения.
            long? window = null;
            if (tabs.Live?.View?.CoreWebView2 is { } live)
                window = JsonNode.Parse(await live.CallDevToolsProtocolMethodAsync("Browser.getWindowForTarget", "{}"))?["windowId"]?.GetValue<long>();
            await view.EnsureCoreWebView2Async(await Tabs.Shared());
            if (open != flyout || view.CoreWebView2 is not { } core)
            {
                view.Close();
                return;
            }
            // До страницы расширения доходит только скрипт отладочного протокола
            // при включённом Page (проверено спайком).
            if (window is { } at)
            {
                await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
                await core.CallDevToolsProtocolMethodAsync("Page.addScriptToEvaluateOnNewDocument", new JsonObject { ["source"] = Script(at) }.ToJsonString());
            }
            core.WindowCloseRequested += (_, _) => flyout.Hide();
            // Ссылка из окна — во вкладку, окно закрывается.
            core.NewWindowRequested += (_, asked) =>
            {
                asked.Handled = true;
                if (Uri.TryCreate(asked.Uri, UriKind.Absolute, out var to)) tabs.Open(to);
                flyout.Hide();
            };
            var first = true;
            core.NavigationCompleted += async (_, _) =>
            {
                // Страница дорисовывается после загрузки: меряем ещё дважды.
                foreach (var wait in new[] { 0, 250, 750 })
                {
                    await Task.Delay(wait);
                    if (open != flyout) return;
                    if (JsonNode.Parse(await core.ExecuteScriptAsync(Measure)) is JsonArray { Count: 2 } size)
                        (view.Width, view.Height) = (size[0]!.GetValue<double>(), size[1]!.GetValue<double>());
                    view.Opacity = 1;
                }
                // Фокус окну — когда страница уже спросила, где она. Фоновая
                // страница расширения (uBlock) ищет вкладку как «текущую в
                // последнем окне с фокусом»; до этого фокус держит страница сайта.
                if (!first) return;
                first = false;
                view.IsTabStop = true;
                view.Focus(FocusState.Programmatic);
            };
            core.Navigate(page);
        }
        catch (Exception e) when (e is COMException or JsonException or InvalidOperationException or FormatException)
        {
            // Движок не ответил или окно закрыли на полпути.
            flyout.Hide();
        }
    }

    /// Запросы «текущего окна» из окна расширения уходят к окну живой вкладки.
    // ponytail: подменён только chrome.tabs.query; tabs.getCurrent и
    // windows.getCurrent отвечают про само окно. Мешает — подменить и их тут же.
    static string Script(long window) => $$"""
        (() => {
          const tabs = globalThis.chrome && chrome.tabs;
          if (!tabs || !tabs.query) return;
          const query = tabs.query.bind(tabs);
          tabs.query = (info, done) => {
            if (info && (info.currentWindow || info.lastFocusedWindow)) {
              info = Object.assign({}, info, { windowId: {{window}} });
              delete info.currentWindow;
              delete info.lastFocusedWindow;
            }
            return query(info, done);
          };
        })();
        """;

    /// Размер окна по странице: ширина — по содержимому без переносов, высота —
    /// при ней; от 25 до 800 на 600, как у Chrome и апстрима.
    // ponytail: страница высотой 100 % окна не сжимается ниже 240 точек.
    // Мешает — мерить, как апстрим, с подменой ширины и высоты страницы.
    const string Measure = """
        (() => {
          const d = document.documentElement, b = document.body;
          if (!b) return null;
          const was = d.style.width;
          d.style.width = 'max-content';
          const w = d.getBoundingClientRect().width;
          d.style.width = was;
          const h = Math.max(d.getBoundingClientRect().height, b.scrollHeight);
          return [Math.min(800, Math.max(25, Math.ceil(w))), Math.min(600, Math.max(25, Math.ceil(h)))];
        })()
        """;

    // Без полей и без предела ширины по умолчанию: размер задаёт страница.
    // Им же рисуется карточка сайта (SiteCard).
    internal static readonly Style Plain = Presenter();

    static Style Presenter()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 800.0));
        style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 600.0));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(Metrics.CardRadius)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Palette.Ground));
        return style;
    }
}
