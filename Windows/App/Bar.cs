using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Browser;

/// Адресная строка в полосе окна (просьба владельца 2026-11-20: «минималистичный
/// URL-бар, который берёт метаданные сайта»): значок сайта и заголовок страницы;
/// под указателем вместо заголовка — адрес (просьба владельца того же дня:
/// «ссылку не показывать по умолчанию, а только при наведении»). Нажатие
/// открывает поле адреса, как Ctrl+L. На пустой вкладке — подсказка. В апстриме
/// такой строки нет (поле всплывает). Вид — поле, как в Helium (просьба
/// владельца 2026-11-21: строка шириной по тексту прыгала под указателем):
/// плашка видна всегда, ширина постоянная — как у всплывающего поля, пока
/// хватает места, — значок и текст слева, длинное обрезается многоточием.
/// Слева от строки на странице сайта — кнопка карточки сайта (SiteCard;
/// просьба владельца 2026-11-20: «кнопка управления правами и данными сайта»).
sealed class Bar
{
    /// Строка целиком: кнопка сайта и сама адресная строка.
    public readonly Grid View;
    public readonly Button Site, Edit;
    readonly Mark mark = new(14);
    readonly Icon find = Ui.Glyph(Ui.Find, 13, Palette.Muted);
    // Строка обрезана по буквам, как в капсулах (Ui.Pill): иначе текст стоит
    // ниже значков на точку-две.
    readonly TextBlock title = new()
    {
        FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, TextLineBounds = TextLineBounds.Tight,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 2),
    };
    readonly Grid line;
    string label = "", link = "";
    bool blank = true, hovering;

    public Bar(Action open, Action site)
    {
        line = new Grid
        {
            Height = Metrics.Door, CornerRadius = new CornerRadius(Metrics.DoorRadius),
            Padding = new Thickness(10, 0, 12, 0), ColumnSpacing = 8,
        };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        mark.VerticalAlignment = find.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(mark);
        line.Children.Add(find);
        Grid.SetColumn(title, 1);
        line.Children.Add(title);

        // Щелчок мышью фокус не берёт, как у кнопок полосы: его заберёт поле.
        Edit = Ui.Bare(new Button
        {
            CornerRadius = new CornerRadius(Metrics.DoorRadius), AllowFocusOnInteraction = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
        }, "Search or enter address", line);
        Edit.PointerEntered += (_, _) => Hover(true);
        Edit.PointerExited += (_, _) => Hover(false);
        Edit.Click += (_, _) => open();
        Site = MainWindow.Door(Ui.Sliders, "Site settings", site);
        // Растянута по месту между кнопками полосы, но не шире поля: уже места
        // — ужимается вместе с ним, шире — стоит по середине.
        View = new Grid
        {
            MaxWidth = Metrics.FieldWidth, ColumnSpacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
        };
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        View.ColumnDefinitions.Add(new ColumnDefinition());
        View.Children.Add(Site);
        Grid.SetColumn(Edit, 1);
        View.Children.Add(Edit);
    }

    /// Указатель над строкой или ушёл (и тест-канал: тихое окно указателя не видит).
    public void Hover(bool on)
    {
        hovering = on;
        Paint();
    }

    /// Привести к живой вкладке.
    public void Show(Tab? tab)
    {
        blank = tab is null or { IsBlank: true };
        label = blank ? "Search or enter address" : tab!.Label;
        link = !blank && tab!.Page is { } page ? Address.Editable(page) : "";
        if (!blank) mark.Show(tab!.Page, tab.Monogram);
        // Сайт — для диктора, и не второй раз, если заголовка нет и в нём уже адрес.
        var host = !blank && !tab!.IsOwn && tab.Page is { Scheme: "http" or "https" } site ? Address.WithoutWWW(site.Host) : "";
        if (host.StartsWith(label, StringComparison.OrdinalIgnoreCase) || label.StartsWith(host, StringComparison.OrdinalIgnoreCase)) host = "";
        AutomationProperties.SetName(Edit, blank ? "Search or enter address"
            : host.Length > 0 ? $"{label}, {host}. Edit address" : $"{label}. Edit address");
        // Карточке сайта есть дело только у страницы сайта: у своих страниц и
        // у пустой вкладки нет ни разрешений, ни куки.
        Site.Visibility = !blank && !tab!.IsOwn && tab.Page is { Scheme: "http" or "https" } ? Visibility.Visible : Visibility.Collapsed;
        Paint();
    }

    void Paint()
    {
        find.Visibility = blank ? Visibility.Visible : Visibility.Collapsed;
        mark.Visibility = blank ? Visibility.Collapsed : Visibility.Visible;
        var address = hovering && !blank && link.Length > 0;
        title.Text = address ? link : label;
        title.Foreground = blank || address ? Palette.Muted : Palette.Ink;
        // Плашка видна и без указателя — так видно, где поле; под указателем темнее.
        line.Background = hovering ? Palette.SideWash : Palette.SideHover;
    }
}
