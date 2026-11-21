// Path из Shapes спорит именем с System.IO.Path, поэтому у значка своё имя.
global using Icon = Microsoft.UI.Xaml.Shapes.Path;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Browser;

// Перенос Palette и Metrics из Sources/Search/Design.swift. Числа те же.
//
// Каждый цвет — пара: для светлого окна и для тёмного. Кисть на каждый цвет
// одна на всё приложение, и тему меняет Apply: он переписывает цвет кисти, а
// всё, что ею нарисовано, перекрашивается само. Больше нигде в коде не
// известно, какая сейчас тема.
static class Palette
{
    static readonly List<(SolidColorBrush Brush, Color Light, Color Dark)> Pairs = [];

    /// Страница и всё, что лежит на белом: карточка, поле адреса, его список.
    public static readonly SolidColorBrush Ground = Grey(1.0, 0.11);
    public static readonly SolidColorBrush Ink = Grey(0.09, 0.93);
    public static readonly SolidColorBrush Muted = Grey(0.55, 0.58);
    public static readonly SolidColorBrush Faint = Grey(0.83, 0.32);
    public static readonly SolidColorBrush Hairline = Grey(0.91, 0.20);
    /// Выбранная строка и строка под указателем — на белом.
    public static readonly SolidColorBrush Wash = Grey(0.937, 0.175);
    public static readonly SolidColorBrush Hover = Grey(0.965, 0.15);
    /// Вокруг карточки страницы: колонка и поля окна, чуть в сторону от белого.
    public static readonly SolidColorBrush Frame = Grey(0.955, 0.075);
    /// Те же два серых для колонки: на её фоне обычные почти пропадали.
    public static readonly SolidColorBrush SideWash = Grey(0.895, 0.155);
    public static readonly SolidColorBrush SideHover = Grey(0.915, 0.13);

    /// Чернила вполсилы: заголовок под указателем.
    public static readonly SolidColorBrush InkSoft = Grey(0.09, 0.93, 0.7);
    /// Подсказка в пустом поле и серое дописывание после каретки.
    public static readonly SolidColorBrush InkGhost = Grey(0.09, 0.93, 0.3);
    /// Плитка под буквой сайта и кружок под крестиком.
    public static readonly SolidColorBrush InkTile = Grey(0.09, 0.93, 0.06);
    public static readonly SolidColorBrush InkDisc = Grey(0.09, 0.93, 0.07);
    /// Край карточки: чёрный или белый на 8%.
    public static readonly SolidColorBrush CardEdge = Grey(0, 1, 0.08);
    /// Страница под полем адреса: она на месте, просто не мешает.
    public static readonly SolidColorBrush Veil = Grey(1.0, 0.11, 0.74);
    /// «Убрать»: красный в три четверти силы, один на обе темы.
    public static readonly SolidColorBrush Danger = new(Color.FromArgb(191, 232, 60, 50));
    /// Прозрачная, но ловит указатель: у элемента без фона нет наведения.
    public static readonly SolidColorBrush Clear = new(Colors.Transparent);

    static SolidColorBrush Grey(double light, double dark, double alpha = 1)
    {
        static Color Shade(double white, double alpha)
        {
            var level = (byte)Math.Round(white * 255);
            return Color.FromArgb((byte)Math.Round(alpha * 255), level, level, level);
        }
        var brush = new SolidColorBrush(Shade(light, alpha));
        Pairs.Add((brush, Shade(light, alpha), Shade(dark, alpha)));
        return brush;
    }

    public static void Apply(bool dark)
    {
        foreach (var (brush, light, night) in Pairs) brush.Color = dark ? night : light;
    }
}

static class Metrics
{
    /// Полоса сверху: за неё окно таскают, в ней кнопки окна и «назад, вперёд,
    /// обновить». На macOS это 52 пункта под «светофор»; здесь — высота высокой
    /// системной полосы (TitleBarHeightOption.Tall): кнопки окна той же высоты,
    /// и всё в полосе стоит на одной средней линии (просьба владельца 2026-11-20:
    /// «лайн не ровный»; при 40 кнопки окна стояли на 4 точки выше остального).
    public const double Band = 48;
    /// Колонка заголовков слева.
    public const double Side = 232;
    public const double SidePadding = 10;
    /// Строка вкладки и зазор между строками.
    public const double Row = 28;
    public const double RowRadius = 9;
    public const double TabGap = 2;
    public const double RowTitle = 12.5;
    /// Буква сайта: квадрат, скруглённый на 22% стороны, буква в 56% стороны.
    public const double Mark = 15;
    /// Кнопка в полосе.
    public const double Door = 26;
    public const double DoorRadius = 8;
    /// Страница — карточка: на столько от краёв окна, с такими углами.
    public const double CardGap = 8;
    public const double CardRadius = 10;
    /// Поле адреса.
    public const double FieldWidth = 560;
    /// Рельс разделов на странице настроек (Settings.swift, rail).
    public const double SettingsRail = 196;
    public const double FieldHeight = 50;
    public const double FieldRadius = 14;
    public const double FieldText = 15.5;
}

static class Ui
{
    // Значки — Lucide (лицензия ISC, lucide.dev): контуры на сетке 24, рисуются
    // штрихом, поэтому толщину можно подобрать под вес текста. SF Symbols с macOS
    // переносить нельзя (лицензия Apple), а у системного шрифта значков Windows
    // вес один, и на Windows 10 этого шрифта нет.
    //
    // В строке склеены все контуры одного значка; rect и circle из файлов Lucide
    // переписаны контурами. Новый значок: взять d из его файла и начало каждого
    // контура «m x y …» записать как «M x y l …», иначе склейка съедет.
    public const string Back = "M12 19l-7-7 7-7M19 12H5", Forward = "M5 12h14M12 5l7 7-7 7",
        Reload = "M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8M21 3v5h-5",
        Plus = "M5 12h14M12 5v14", Cross = "M18 6L6 18M6 6l12 12", Down = "M6 9l6 6 6-6";

    const string Pane = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM9 3v18";
    /// Колонку убрать и колонку вернуть.
    public const string ClosePane = Pane + "M16 15l-3-3 3-3", OpenPane = Pane + "M14 9l3 3-3 3";

    /// Значки своих страниц: настройки и история.
    public const string Gear = "M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0z",
        Find = "M21 21l-4.34-4.34M3 11a8 8 0 1 0 16 0a8 8 0 1 0-16 0z",
        Guard = "M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z",
        Colour = "M12 22a1 1 0 0 1 0-20 10 9 0 0 1 10 9 5 5 0 0 1-5 5h-2.25a1.75 1.75 0 0 0-1.4 2.8l.3.4a1.75 1.75 0 0 1-1.4 2.8zM13 6.5a.5 .5 0 1 0 1 0a.5 .5 0 1 0-1 0zM17 10.5a.5 .5 0 1 0 1 0a.5 .5 0 1 0-1 0zM6 12.5a.5 .5 0 1 0 1 0a.5 .5 0 1 0-1 0zM8 7.5a.5 .5 0 1 0 1 0a.5 .5 0 1 0-1 0z",
        // Строки настроек: у каждой свой значок, а не значок её раздела.
        SunMoon = "M12 2v2M14.837 16.385a6 6 0 1 1-7.223-7.222c.624-.147.97.66.715 1.248a4 4 0 0 0 5.26 5.259c.589-.255 1.396.09 1.248.715M16 12a4 4 0 0 0-4-4M19 5l-1.256 1.256M20 12h2",
        TextSearch = "M21 5H3M10 12H3M10 19H3M14 15a3 3 0 1 0 6 0a3 3 0 1 0-6 0zM21 19l-1.9-1.9",
        ShieldBan = Guard + "M4.243 5.21l14.39 12.472",
        ShieldCheck = Guard + "M9 12l2 2 4-4",
        Cookie = "M12 2a10 10 0 1 0 10 10 4 4 0 0 1-5-5 4 4 0 0 1-5-5M8.5 8.5v.01M16 15.5v.01M12 12v.01M11 17v.01M7 14v.01",
        EyeOff = "M10.733 5.076a10.744 10.744 0 0 1 11.205 6.575 1 1 0 0 1 0 .696 10.747 10.747 0 0 1-1.444 2.49M14.084 14.158a3 3 0 0 1-4.242-4.242M17.479 17.499a10.75 10.75 0 0 1-15.417-5.151 1 1 0 0 1 0-.696 10.75 10.75 0 0 1 4.446-5.143M2 2l20 20",
        KeyRound = "M2.586 17.414A2 2 0 0 0 2 18.828V21a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h1a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h.172a2 2 0 0 0 1.414-.586l.814-.814a6.5 6.5 0 1 0-4-4zM16 7.5a.5 .5 0 1 0 1 0a.5 .5 0 1 0-1 0z",
        TextCursorInput = "M12 20h-1a2 2 0 0 1-2-2 2 2 0 0 1-2 2H6M13 8h7a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2h-7M5 16H4a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2h1M6 4h1a2 2 0 0 1 2 2 2 2 0 0 1 2-2h1M9 6v12",
        Sliders = "M10 5H3M12 19H3M14 3v4M16 17v4M21 12h-9M21 19h-5M21 5h-7M8 10v4M8 12H3",
        Store = "M15 21v-5a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v5M17.774 10.31a1.12 1.12 0 0 0-1.549 0 2.5 2.5 0 0 1-3.451 0 1.12 1.12 0 0 0-1.548 0 2.5 2.5 0 0 1-3.452 0 1.12 1.12 0 0 0-1.549 0 2.5 2.5 0 0 1-3.77-3.248l2.889-4.184A2 2 0 0 1 7 2h10a2 2 0 0 1 1.653.873l2.895 4.192a2.5 2.5 0 0 1-3.774 3.244M4 10.95V19a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8.05",
        FolderOpen = "M6 14l1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2",
        Cpu = "M12 20v2M12 2v2M17 20v2M17 2v2M2 12h2M2 17h2M2 7h2M20 12h2M20 17h2M20 7h2M7 20v2M7 2v2M6 4h12a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zM9 8h6a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H9a1 1 0 0 1-1-1V9a1 1 0 0 1 1-1z",
        Flask = "M14 2v6a2 2 0 0 0 .245.96l5.51 10.08A2 2 0 0 1 18 22H6a2 2 0 0 1-1.755-2.96l5.51-10.08A2 2 0 0 0 10 8V2M6.453 15h11.094M8.5 2h7",
        // История: выбранные строки и их удаление.
        Trash = "M10 11v6M14 11v6M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2",
        Check = "M20 6L9 17l-5-5",
        Globe = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0zM12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20M2 12h20",
        Clock = "M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8M3 3v5h5M12 7v5l4 2",
        Puzzle = "M15.39 4.39a1 1 0 0 0 1.68-.474 2.5 2.5 0 1 1 3.014 3.015 1 1 0 0 0-.474 1.68l1.683 1.682a2.414 2.414 0 0 1 0 3.414L19.61 15.39a1 1 0 0 1-1.68-.474 2.5 2.5 0 1 0-3.014 3.015 1 1 0 0 1 .474 1.68l-1.683 1.682a2.414 2.414 0 0 1-3.414 0L8.61 19.61a1 1 0 0 0-1.68.474 2.5 2.5 0 1 1-3.014-3.015 1 1 0 0 0 .474-1.68l-1.683-1.682a2.414 2.414 0 0 1 0-3.414L4.39 8.61a1 1 0 0 1 1.68.474 2.5 2.5 0 1 0 3.014-3.015 1 1 0 0 1-.474-1.68l1.683-1.682a2.414 2.414 0 0 1 3.414 0z",
        More = "M11 12a1 1 0 1 0 2 0a1 1 0 1 0-2 0zM18 12a1 1 0 1 0 2 0a1 1 0 1 0-2 0zM4 12a1 1 0 1 0 2 0a1 1 0 1 0-2 0z",
        Keyboard = "M10 8h.01M12 12h.01M14 8h.01M16 12h.01M18 8h.01M6 8h.01M7 16h10M8 12h.01M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z",
        Focus = "M3 7V5a2 2 0 0 1 2-2h2M17 3h2a2 2 0 0 1 2 2v2M21 17v2a2 2 0 0 1-2 2h-2M7 21H5a2 2 0 0 1-2-2v-2",
        Info = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0zM12 16v-4M12 8h.01",
        // Сон вкладок: раздел Tabs (copy) и строка (moon).
        TwoTabs = "M10 8h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H10a2 2 0 0 1-2-2V10a2 2 0 0 1 2-2zM4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2",
        Moon = "M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9z",
        // Загрузки: страница и кнопка в полосе (download), скачанный файл (file).
        Download = "M12 15V3M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5",
        File = "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7zM14 2v4a2 2 0 0 0 2 2h4",
        // Закладки: кнопка в полосе (залита на сохранённой странице) и страница.
        Bookmark = "M19 21l-7-4-7 4V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2v16z";

    /// Значок со стороной size. weight — толщина штриха в единицах сетки
    /// (2 — как в наборе): мелким значкам нужен штрих толще, иначе они тают.
    public static Icon Glyph(string outline, double size, Brush colour, double weight = 2)
    {
        var icon = new Icon
        {
            Width = size, Height = size, Stroke = colour, StrokeThickness = size * weight / 24,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        Draw(icon, outline);
        return icon;
    }

    /// Другой рисунок тому же значку: кнопка колонки меняет его на ходу.
    public static void Draw(Icon icon, string outline)
    {
        // Геометрию из строки делает сам XAML: тот же разбор, что у Path.Data в разметке.
        var shape = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), outline);
        shape.Transform = new ScaleTransform { ScaleX = icon.Height / 24, ScaleY = icon.Height / 24 };
        icon.Data = shape;
    }

    /// Стандартное поле Windows рисует рамку, подчёркивание и кнопку очистки.
    /// С этим шаблоном от него остаётся только текст с кареткой.
    public static ControlTemplate PlainText => (ControlTemplate)XamlReader.Load("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TextBox">
            <ScrollViewer x:Name="ContentElement" IsTabStop="False" ZoomMode="Disabled"
                          HorizontalScrollMode="Auto" HorizontalScrollBarVisibility="Hidden"
                          VerticalScrollMode="Disabled" VerticalScrollBarVisibility="Hidden" />
        </ControlTemplate>
        """);

    /// Кнопка, переключатель или выбор из нескольких, нарисованные нами. От
    /// стандартного элемента остаётся то, что самим не нарисовать: фокус с
    /// клавиатуры, нажатие пробелом и имя, которое читает экранный диктор.
    /// Шаблон показывает только содержимое.
    public static T Bare<T>(T control, string name, UIElement content) where T : ContentControl
    {
        control.Template = (ControlTemplate)XamlReader.Load($$"""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="{{typeof(T).Name}}">
                <ContentPresenter Content="{TemplateBinding Content}" />
            </ControlTemplate>
            """);
        control.Content = content;
        // Стандартный стиль оставляет элементу поля и наименьшую ширину.
        control.Padding = new Thickness(0);
        control.MinWidth = control.MinHeight = 0;
        AutomationProperties.SetName(control, name);
        return control;
    }

    // Части своих страниц: настроек и истории.

    /// Заголовок своей страницы: значок и имя.
    public static StackPanel Heading(string glyph, string name)
    {
        var title = new TextBlock { Text = name, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Palette.Ink };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        heading.Children.Add(Glyph(glyph, 18, Palette.Ink));
        heading.Children.Add(title);
        return heading;
    }

    /// Маленькая капсула, которая делает одно дело. Pill из апстрима.
    public static Button Pill(string name, UIElement label, Action? act = null)
    {
        // Надпись посередине по своим буквам (Word), а не по строке шрифта: с
        // равными полями сверху и снизу буквы стояли на точку ниже середины. И
        // по буквам Segoe UI встаёт на точку ниже — поле снизу на две точки больше
        // (замерено на снимке: от середины капсулы не дальше полточки).
        if (label is FrameworkElement middle) middle.VerticalAlignment = VerticalAlignment.Center;
        var capsule = new Border
        {
            Height = 26, CornerRadius = new CornerRadius(13), Padding = new Thickness(10, 0, 10, 2), Background = Palette.Ground,
            BorderBrush = Palette.Hairline, BorderThickness = new Thickness(1), Child = label,
        };
        var pill = Bare(new Button { CornerRadius = new CornerRadius(13) }, name, capsule);
        pill.PointerEntered += (_, _) => capsule.Background = Palette.Hover;
        pill.PointerExited += (_, _) => capsule.Background = Palette.Ground;
        if (act is not null) pill.Click += (_, _) => act();
        return pill;
    }

    /// Надпись капсулы. Её место — от верха прописных до базовой линии
    /// (TextLineBounds.Tight): посередине встают буквы, а не строка шрифта.
    public static TextBlock Word(string text) => new()
    {
        Text = text, FontSize = 11.5, Foreground = Palette.Ink, VerticalAlignment = VerticalAlignment.Center,
        TextLineBounds = TextLineBounds.Tight,
    };

    /// Включено или выключено, чернилами, а не синим. Switch из апстрима.
    /// changed — по смене состояния, а не по нажатию: экранный диктор
    /// переключает, не нажимая.
    public static ToggleButton Switch(string name, Action<bool> changed)
    {
        var knob = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 14, Height = 14, Fill = Palette.Ground, Margin = new Thickness(2) };
        var track = new Border { Width = 30, Height = 18, CornerRadius = new CornerRadius(9), Child = knob };
        var toggle = Bare(new ToggleButton { CornerRadius = new CornerRadius(9) }, name, track);
        void Paint()
        {
            var on = toggle.IsChecked == true;
            track.Background = on ? Palette.Ink : Palette.Faint;
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
        toggle.Checked += (_, _) => { Paint(); changed(true); };
        toggle.Unchecked += (_, _) => { Paint(); changed(false); };
        Paint();
        return toggle;
    }

    /// Несколько вариантов в серой дорожке, выбранный приподнят белым.
    /// Segmented из апстрима. Show отмечает вариант, не зовя chosen.
    public static (Border View, Action<string> Show) Segmented(string name, (string Value, string Title)[] options, Action<string> chosen)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var marks = new List<(string Value, RadioButton Option)>();
        var quiet = false;
        foreach (var (value, title) in options)
        {
            // Буквы посередине, как в капсуле (Pill).
            var text = new TextBlock { Text = title, FontSize = 11.5, TextLineBounds = TextLineBounds.Tight, VerticalAlignment = VerticalAlignment.Center };
            var lift = new Border { Height = 26, CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 0, 10, 2), Child = text };
            var option = Bare(new RadioButton { GroupName = name, CornerRadius = new CornerRadius(7) }, $"{name}: {title}", lift);
            void Paint()
            {
                var on = option.IsChecked == true;
                lift.Background = on ? Palette.Ground : Palette.Clear;
                text.Foreground = on ? Palette.Ink : Palette.Muted;
                text.FontWeight = on ? FontWeights.Medium : FontWeights.Normal;
            }
            option.Checked += (_, _) =>
            {
                Paint();
                if (!quiet) chosen(value);
            };
            option.Unchecked += (_, _) => Paint();
            Paint();
            marks.Add((value, option));
            line.Children.Add(option);
        }
        void Show(string value)
        {
            quiet = true;
            foreach (var (mark, option) in marks) option.IsChecked = mark == value;
            quiet = false;
        }
        return (new Border { CornerRadius = new CornerRadius(9), Background = Palette.Wash, Padding = new Thickness(2), Child = line }, Show);
    }

    /// Поле в одну строку с подсказкой, пока оно пустое. Без значка — в тонкой
    /// рамке (FindField из апстрима); со значком — серое поле поиска по списку
    /// (Hunt из апстрима).
    public static (Grid Frame, TextBox Box) Entry(string name, string hint, string? glyph = null)
    {
        var hunt = glyph is not null;
        var size = hunt ? 13 : 12.5;
        // Цвет текста не задаём: стандартный сам следует за темой окна.
        var box = new TextBox
        {
            FontSize = size, MinHeight = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            IsSpellCheckEnabled = false, Template = PlainText,
        };
        AutomationProperties.SetName(box, name);
        var ghost = new TextBlock
        {
            Text = hint, FontSize = size, Foreground = Palette.InkGhost, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        box.TextChanged += (_, _) => ghost.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var frame = new Grid
        {
            Height = hunt ? 34 : 28, CornerRadius = new CornerRadius(hunt ? 10 : 8),
            Background = hunt ? Palette.Wash : Palette.Ground,
            BorderBrush = Palette.Hairline, BorderThickness = new Thickness(hunt ? 0 : 1),
            Padding = new Thickness(hunt ? 12 : 9, 0, hunt ? 12 : 9, 0),
        };
        frame.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        frame.ColumnDefinitions.Add(new ColumnDefinition());
        if (glyph is not null)
        {
            var sign = Glyph(glyph, 14, Palette.Muted);
            sign.Margin = new Thickness(0, 0, 8, 0);
            frame.Children.Add(sign);
        }
        Grid.SetColumn(ghost, 1);
        Grid.SetColumn(box, 1);
        frame.Children.Add(ghost);
        frame.Children.Add(box);
        return (frame, box);
    }
}
