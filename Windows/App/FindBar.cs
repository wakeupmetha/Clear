using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Browser;

/// Поиск на странице: капсула в правом верхнем углу страницы — поле, «3 of 17»,
/// предыдущее, следующее, закрыть. FindBar из Sources/Search/Find.swift. Ищет
/// сам движок (CoreWebView2.Find): подсвечивает совпадения и прокручивает к
/// текущему; его собственное окошко поиска подавлено.
// ponytail: без «Match case» и «Whole words» апстрима (его меню «…»). Понадобятся —
// IsCaseSensitive и ShouldMatchWord в CoreWebView2FindOptions.
sealed class FindBar
{
    public readonly Border View;
    readonly Tabs tabs;
    readonly TextBox box;
    readonly TextBlock status = new()
    {
        FontSize = 10, FontWeight = FontWeights.Medium, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center,
    };
    readonly TypedEventHandler<CoreWebView2Find, object> counted;
    // Вкладка и её движок, в котором идёт поиск. Сверяем по вкладке, а не по
    // движку: обёртка движка — объект проекции, на ссылку не опереться.
    // Живая вкладка не засыпает, так что движок у неё тот же.
    Tab? tab;
    CoreWebView2? core;

    public FindBar(Tabs tabs)
    {
        this.tabs = tabs;
        counted = (_, _) => Count();
        var (field, entry) = Ui.Entry("Find on page", "Find on page");
        // Поле без своей рамки: рамка — у капсулы.
        field.BorderThickness = new Thickness(0);
        field.Padding = new Thickness(0);
        field.Background = Palette.Clear;
        field.Width = 150;
        box = entry;
        box.TextChanged += (_, _) => Start();
        box.KeyDown += (_, press) =>
        {
            if (press.Key == VirtualKey.Enter) Look(!InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down));
            else if (press.Key == VirtualKey.Escape) Close();
            else return;
            press.Handled = true;
        };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(field);
        line.Children.Add(status);
        line.Children.Add(Step(Ui.Up, "Previous match", () => Look(false)));
        line.Children.Add(Step(Ui.Down, "Next match", () => Look(true)));
        line.Children.Add(Step(Ui.Cross, "Close Find on Page", Close));
        View = new Border
        {
            CornerRadius = new CornerRadius(20), Padding = new Thickness(16, 4, 6, 4), Child = line,
            Background = Palette.Ground, BorderBrush = Palette.Hairline, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 14, 0), Shadow = new ThemeShadow(), Translation = new Vector3(0, 0, 32),
            Visibility = Visibility.Collapsed,
        };
    }

    public bool IsOpen => View.Visibility == Visibility.Visible;

    /// Для тест-канала: что сказано рядом с полем; null — поиск закрыт.
    public string? Said => IsOpen ? status.Text : null;

    /// Ctrl+F: поле в фокус, набранное выделено. Только на странице сайта:
    /// у своих страниц (история, закладки, настройки) свои поля поиска.
    public void Open()
    {
        if (tabs.Live?.View is null) return;
        View.Visibility = Visibility.Visible;
        box.Focus(FocusState.Programmatic);
        box.SelectAll();
        if (box.Text.Length > 0) Start();
    }

    /// Крестик, Escape: подсветка уходит, клавиатура — обратно в страницу.
    public void Close()
    {
        if (!IsOpen) return;
        View.Visibility = Visibility.Collapsed;
        Forget();
        if (tabs.Live is { } live) tabs.Face(live);
    }

    /// Живая вкладка сменилась: искать на её странице, а у своей — закрыться.
    public void Follow(Tab? live)
    {
        if (!IsOpen || (live == tab && core is not null)) return;
        if (live?.View is null) Close();
        else Start();
    }

    /// Enter и Shift+Enter, стрелки капсулы; Ctrl+G и Ctrl+Shift+G, когда
    /// фокус не в странице (в странице их листает сам движок, Shortcuts).
    public void Look(bool forward)
    {
        if (!IsOpen) { Open(); return; }
        if (core is null || tabs.Live != tab) { Start(); return; }
        try
        {
            if (forward) core.Find.FindNext();
            else core.Find.FindPrevious();
        }
        catch (Exception e) when (e is COMException or ObjectDisposedException) { Forget(); }
    }

    async void Start()
    {
        if (tabs.Live != tab || core is null)
        {
            Forget();
            tab = tabs.Live;
            core = tab?.View?.CoreWebView2;
            if (core is not null)
            {
                core.Find.MatchCountChanged += counted;
                core.Find.ActiveMatchIndexChanged += counted;
            }
        }
        if (core is not { } seeking) return;
        try
        {
            if (box.Text.Length == 0) seeking.Find.Stop();
            else
            {
                var options = seeking.Environment.CreateFindOptions();
                options.FindTerm = box.Text;
                options.SuppressDefaultFindDialog = true;
                options.ShouldHighlightAllMatches = true;
                await seeking.Find.StartAsync(options);
            }
        }
        // Вкладку закрыли, пока искали.
        catch (Exception e) when (e is COMException or ObjectDisposedException) { }
        Count();
    }

    void Count()
    {
        if (core is not { } seeking || box.Text.Length == 0) { status.Text = ""; return; }
        int count;
        try { count = seeking.Find.MatchCount; }
        catch (Exception e) when (e is COMException or ObjectDisposedException) { return; }
        status.Text = count > 0 ? $"{seeking.Find.ActiveMatchIndex} of {count}" : "No matches";
        // Не нашлось — красный край, как в апстриме.
        View.BorderBrush = count > 0 ? Palette.Hairline : Palette.Danger;
    }

    // Поиск в прежнем движке кончается: подсветка уходит со страницы.
    void Forget()
    {
        if (core is { } old)
        {
            try
            {
                old.Find.MatchCountChanged -= counted;
                old.Find.ActiveMatchIndexChanged -= counted;
                old.Find.Stop();
            }
            catch (Exception e) when (e is COMException or ObjectDisposedException) { }
        }
        tab = null;
        core = null;
        status.Text = "";
        View.BorderBrush = Palette.Hairline;
    }

    static Button Step(string glyph, string name, Action act)
    {
        var icon = Ui.Glyph(glyph, 12, Palette.Muted, 2.5);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var disc = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Palette.Clear, Child = icon };
        var step = Ui.Bare(new Button { VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(12) }, name, disc);
        step.PointerEntered += (_, _) => disc.Background = Palette.Hover;
        step.PointerExited += (_, _) => disc.Background = Palette.Clear;
        step.Click += (_, _) => act();
        ToolTipService.SetToolTip(step, name);
        return step;
    }
}
