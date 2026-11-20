using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Browser;

/// Адресная строка в полосе окна (просьба владельца 2026-11-20: «минималистичный
/// URL-бар, который берёт метаданные сайта»): значок сайта, заголовок страницы
/// и серым сайт. Нажатие открывает поле адреса, как Ctrl+L. На пустой вкладке —
/// подсказка. В апстриме такой строки нет (поле всплывает), вид свой: тихий,
/// как кнопки полосы, плашка — только под указателем.
sealed class Bar
{
    public readonly Button View;
    readonly Mark mark = new(14);
    readonly Icon find = Ui.Glyph(Ui.Find, 13, Palette.Muted);
    readonly TextBlock title = new() { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock site = new() { FontSize = 12, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center };

    public Bar(Action open)
    {
        var line = new Grid
        {
            Height = Metrics.Door, CornerRadius = new CornerRadius(Metrics.DoorRadius), Background = Palette.Clear,
            Padding = new Thickness(10, 0, 12, 0), ColumnSpacing = 8,
        };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        find.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(mark);
        line.Children.Add(find);
        Grid.SetColumn(title, 1);
        line.Children.Add(title);
        Grid.SetColumn(site, 2);
        line.Children.Add(site);

        // Щелчок мышью фокус не берёт, как у кнопок полосы: его заберёт поле.
        View = Ui.Bare(new Button
        {
            CornerRadius = new CornerRadius(Metrics.DoorRadius), AllowFocusOnInteraction = false, MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        }, "Search or enter address", line);
        View.PointerEntered += (_, _) => line.Background = Palette.SideHover;
        View.PointerExited += (_, _) => line.Background = Palette.Clear;
        View.Click += (_, _) => open();
    }

    /// Привести к живой вкладке.
    public void Show(Tab? tab)
    {
        var blank = tab is null or { IsBlank: true };
        find.Visibility = blank ? Visibility.Visible : Visibility.Collapsed;
        mark.Visibility = blank ? Visibility.Collapsed : Visibility.Visible;
        title.Text = blank ? "Search or enter address" : tab!.Label;
        title.Foreground = blank ? Palette.Muted : Palette.Ink;
        // Сайт — только у сайтов, и не второй раз, если заголовка нет и в нём уже адрес.
        var host = !blank && !tab!.IsOwn && tab.Page is { Scheme: "http" or "https" } page ? Address.WithoutWWW(page.Host) : "";
        site.Text = host.Length > 0 && !title.Text.StartsWith(host, StringComparison.OrdinalIgnoreCase) ? host : "";
        site.Visibility = site.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!blank) mark.Show(tab!.Page, tab.Monogram);
        AutomationProperties.SetName(View, blank ? "Search or enter address"
            : site.Text.Length > 0 ? $"{title.Text}, {site.Text}. Edit address" : $"{title.Text}. Edit address");
    }
}
