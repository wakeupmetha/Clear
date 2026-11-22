using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using static Browser.Ui;

namespace Browser;

/// История: своя страница во вкладке (browser://history). Вид перенесён из
/// HistoryPanel в Sources/Search/Recall.swift: поле поиска, под ним дни, у
/// каждого дня карточка мест, внизу сколько их и «Clear…».
///
/// Отличие от апстрима: место открывается в новой вкладке, а история остаётся
/// открытой. Там оно открывается в той же вкладке, и «назад» возвращает к
/// истории; здесь у своей страницы нет движка, а с ним и «назад». Ещё здесь
/// можно выбрать несколько мест и убрать их разом (просьба владельца): Ctrl+щелчок,
/// Shift+щелчок, Ctrl+A в списке.
sealed class HistoryPage : Grid
{
    readonly Tabs tabs;
    readonly TextBox hunt;
    readonly ItemsRepeater list = new() { Layout = new StackLayout() };
    readonly ScrollViewer scroll;
    readonly Border nothing;
    readonly TextBlock none = new() { FontSize = 13, Foreground = Palette.Muted };
    readonly TextBlock count = new() { FontSize = 12, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center };
    readonly FrameworkElement sweeps;
    readonly Button clear, done, deselect, drop;
    readonly Grid line;
    List<History.Line> lines = [];
    // Выбранные места: ключи, а не строки — строка служит разным местам по
    // очереди (Maker). anchor — от него Shift+щелчок берёт диапазон.
    readonly HashSet<string> chosen = [];
    string? anchor;
    bool clearing;

    public HistoryPage(Tabs tabs)
    {
        this.tabs = tabs;
        Background = Palette.Ground;
        // Подвал идёт сразу за списком, а не у нижнего края страницы: при
        // коротком списке между ними не остаётся пустого поля. Сетка стоит по
        // верху, поэтому строка «*» берёт столько, сколько нужно списку, но не
        // больше, чем есть: длинный список прокручивается, подвал виден.
        var body = new Grid { VerticalAlignment = VerticalAlignment.Top };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(body);

        var (frame, box) = Entry("Search history", "Search everywhere you have been", Ui.Find);
        hunt = box;
        hunt.TextChanged += (_, _) => Refresh();
        var head = new StackPanel { Spacing = 16 };
        head.Children.Add(Heading(Ui.Clock, "History"));
        head.Children.Add(frame);
        body.Children.Add(Centred(head, 26, 14));

        // Прокрутка во всю ширину страницы, узкая только колонка в ней: пустое
        // место по бокам тоже должно прокручивать список.
        list.ItemTemplate = new Maker(this);
        var column = Centred(list, 0, 14);
        column.HorizontalAlignment = HorizontalAlignment.Center;
        scroll = new ScrollViewer
        {
            Content = column, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        };
        // Ширину колонке в прокрутке задаём сами. Без этого она шириной с самый
        // длинный заголовок в виду, и при коротких заголовках прокрутка ставит
        // её правее поля поиска.
        scroll.SizeChanged += (_, _) => column.Width = Math.Min(600, scroll.ActualWidth);
        // Ctrl+A в списке выбирает все места в виду, Esc снимает выбор. Поле
        // поиска своё Ctrl+A оставляет себе: оно не в прокрутке.
        scroll.KeyDown += (_, press) =>
        {
            if (press.Key == VirtualKey.A && Held().HasFlag(VirtualKeyModifiers.Control)) ChooseAll(true);
            else if (press.Key == VirtualKey.Escape && chosen.Count > 0) ChooseAll(false);
            else return;
            press.Handled = true;
        };
        SetRow(scroll, 1);
        body.Children.Add(scroll);

        nothing = Centred(new Border
        {
            CornerRadius = new CornerRadius(11), BorderBrush = Palette.Hairline, BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 18, 14, 18), Child = none,
        }, 0, 14);
        nothing.VerticalAlignment = VerticalAlignment.Top;
        SetRow(nothing, 1);
        body.Children.Add(nothing);

        // Подвал — одна строка: слева что здесь (сколько мест, сколько выбрано),
        // справа что сделать. «Clear…» открывает вместо неё карточку очистки со
        // своим заголовком и «Done» (просьба владельца 2026-11-20: «Back» снизу не
        // одинокий; кнопки в карточке одной ширины). Само удаление — второе
        // нажатие, уже в карточке: его не вернуть, одного нажатия для него мало.
        clear = Pill("Choose what to clear", Word("Clear…"), () => Clearing(true));
        done = Pill("Done", Word("Done"), () => Clearing(false));
        // После «Deselect» и «Remove» обе прячутся: фокус — на вернувшуюся «Clear…».
        deselect = Pill("Deselect", Word("Deselect"), () => { ChooseAll(false); clear.Focus(FocusState.Programmatic); });
        var removal = Word("Remove");
        removal.Foreground = Palette.Danger;
        drop = Pill("Remove selected", removal, () => { Forget(chosen, keyed: false); clear.Focus(FocusState.Programmatic); });
        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var act in new[] { deselect, drop, clear }) acts.Children.Add(act);
        line = new Grid { Margin = new Thickness(0, 14, 0, 14) };
        line.Children.Add(count);
        line.Children.Add(acts);

        // Одна карточка: заголовок с «Done» и три строки с одинаковыми «Clear».
        // После куки и кэша карточка не закрывается: их можно стереть одну за
        // другой, не возвращаясь к «Clear…».
        var card = new StackPanel();
        var heading = new Grid { Padding = new Thickness(14, 10, 14, 10) };
        heading.Children.Add(new TextBlock
        {
            Text = "Clear browsing data", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Palette.Ink,
            VerticalAlignment = VerticalAlignment.Center,
        });
        done.HorizontalAlignment = HorizontalAlignment.Right;
        heading.Children.Add(done);
        card.Children.Add(heading);
        card.Children.Add(new Border { Height = 1, Background = Palette.Hairline });
        card.Children.Add(Sweep("History", "Everywhere you have been", Pill("Clear history", Word("Clear"), () =>
        {
            tabs.History.Forget();
            Mark.ForgetAll();
            Clearing(false);
            Refresh();
        })));
        card.Children.Add(new Border { Height = 1, Background = Palette.Hairline });
        card.Children.Add(Sweep("Cookies and sign-ins", "Signs you out of every site",
            Pill("Sign out of everything", Word("Clear"), () => _ = tabs.Sweep(Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.AllSite))));
        card.Children.Add(new Border { Height = 1, Background = Palette.Hairline });
        card.Children.Add(Sweep("Cache", "Only what was fetched to draw pages",
            Pill("Clear cache", Word("Clear"), () => _ = tabs.Sweep(Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.DiskCache))));
        sweeps = new Border
        {
            CornerRadius = new CornerRadius(11), BorderBrush = Palette.Hairline, BorderThickness = new Thickness(1), Child = card,
            Margin = new Thickness(0, 14, 0, 14),
        };

        var foot = new StackPanel();
        foot.Children.Add(new Border { Height = 1, Background = Palette.Hairline });
        foot.Children.Add(sweeps);
        foot.Children.Add(line);
        var below = Centred(foot, 0, 0);
        SetRow(below, 2);
        body.Children.Add(below);
        Foot();

        Loaded += (_, _) => Arrive();
        // К странице вернулись с другой вкладки: за это время там могли
        // побывать в новых местах.
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => { if (Visibility == Visibility.Visible) Arrive(); });
    }

    /// Колонка шириной с карточку настроек, по середине страницы.
    static Border Centred(UIElement child, double top, double bottom) =>
        new() { MaxWidth = 600, Padding = new Thickness(24, top, 24, bottom), Child = child };

    static Visibility Seen(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    /// Ctrl и Shift, как они нажаты сейчас. Из события их не взять: щелчок,
    /// Enter и пробел на строке приходят одним Click.
    static VirtualKeyModifiers Held() =>
        (Down(VirtualKey.Control) ? VirtualKeyModifiers.Control : 0) | (Down(VirtualKey.Shift) ? VirtualKeyModifiers.Shift : 0);

    static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    void Arrive()
    {
        Refresh();
        hunt.Focus(FocusState.Programmatic);
    }

    void Refresh()
    {
        var traces = tabs.History.Everything(hunt.Text);
        lines = History.Lines(traces, DateTime.Now);
        // Выбор держится только за места в виду: убранное и скрытое поиском
        // кнопка «Remove» не тронет.
        chosen.IntersectWith(traces.Select(trace => trace.Key));
        if (chosen.Count == 0) anchor = null;
        list.ItemsSource = lines;
        var empty = traces.Count == 0;
        scroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        nothing.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        none.Text = hunt.Text.Trim().Length == 0 ? "Nothing yet." : "Nothing matches.";
        Foot();
    }

    /// Подвал по состоянию: очистка, выбранные места или сколько их всего.
    void Foot()
    {
        var picking = chosen.Count > 0 && !clearing;
        var pages = lines.Count(line => line.Page is not null);
        // Пустой список уже говорит «Nothing yet.»: «0 pages» под ним — второй раз о том же.
        count.Text = picking ? $"{chosen.Count} selected" : pages switch { 0 => "", 1 => "1 page", _ => $"{pages} pages" };
        sweeps.Visibility = Seen(clearing);
        line.Visibility = Seen(!clearing);
        clear.Visibility = Seen(!picking);
        deselect.Visibility = drop.Visibility = Seen(picking);
    }

    /// Нажатие на место. Ctrl — выбрать его или снять, Shift — выбрать всё от
    /// прошлого выбранного до него; без них — открыть.
    void Press(History.Trace place, VirtualKeyModifiers with)
    {
        if (with.HasFlag(VirtualKeyModifiers.Shift) && anchor is not null)
            chosen.UnionWith(History.Between(lines, anchor, place.Key));
        else if (with != VirtualKeyModifiers.None)
        {
            if (!chosen.Remove(place.Key)) chosen.Add(place.Key);
            anchor = place.Key;
        }
        else
        {
            tabs.Open(place.Url);
            return;
        }
        Chosen();
    }

    /// Все места в виду или ни одного.
    void ChooseAll(bool all)
    {
        chosen.Clear();
        if (all) chosen.UnionWith(lines.Where(line => line.Page is not null).Select(line => line.Page!.Key));
        anchor = null;
        Chosen();
    }

    /// Выбор сменился: строки в виду и подвал перерисовываются.
    void Chosen()
    {
        foreach (var row in Rows()) row.Paint();
        Foot();
    }

    /// Строки, которые список сейчас показывает (отданные в запас спрятаны).
    IEnumerable<Row> Rows()
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(list); i++)
            if (VisualTreeHelper.GetChild(list, i) is Row { Visibility: Visibility.Visible } row) yield return row;
    }

    /// Тест-канал: Ctrl+щелчок (shift — Shift+щелчок) по строке с этим именем,
    /// all — Ctrl+A, none — Esc. Настоящий Ctrl в тихое окно не послать.
    /// Отдаёт, сколько мест выбрано.
    public int Choose(string? name, bool shift, bool all, bool none)
    {
        if (all || none) ChooseAll(all);
        else if (Rows().FirstOrDefault(row => row.Called == name)?.Place is { } place)
            Press(place, shift ? VirtualKeyModifiers.Shift : VirtualKeyModifiers.Control);
        else throw new ArgumentException("no such place");
        return chosen.Count;
    }

    /// Убирает места из истории. keyed — убрали клавишей: фокус был в списке
    /// и остаётся в нём, на строке, вставшей на место первой убранной.
    void Forget(IEnumerable<string> keys, bool keyed)
    {
        var gone = keys.ToHashSet();
        var at = lines.FindIndex(line => line.Page is { } page && gone.Contains(page.Key));
        foreach (var key in gone) tabs.History.Forget(key);
        Refresh();
        if (!keyed) return;
        list.UpdateLayout();
        // На этом месте теперь следующая строка, имя следующего дня или конец списка.
        var next = new[] { at, at + 1, at - 1 }.Where(i => i >= 0 && i < lines.Count)
            .Select(i => list.TryGetElement(i)).OfType<Row>().FirstOrDefault();
        if (next is null) hunt.Focus(FocusState.Keyboard);
        else next.Take();
    }

    /// Карточка очистки: показать (Clear… и Ctrl+Shift+Delete) или убрать.
    public void Clearing(bool on)
    {
        clearing = on;
        Foot();
        // Нажатая кнопка спряталась: фокус уходит на ту, что её сменила. К
        // «Done», а не к «Clear»: второй Enter подряд не должен ничего стирать.
        (on ? done : clear).Focus(FocusState.Programmatic);
    }

    /// Одна строка карточки очистки: что это слева, кнопка справа. Строки
    /// sweeps из апстрима; карточку собирает конструктор.
    static Grid Sweep(string title, string detail, Button act)
    {
        var words = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Palette.Ink });
        words.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap });
        var line = new Grid { Padding = new Thickness(14, 10, 14, 10), ColumnSpacing = 16 };
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        act.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(act, 1);
        line.Children.Add(words);
        line.Children.Add(act);
        return line;
    }

    /// Делает строки списка по мере того, как они входят в вид, и забирает
    /// ушедшие в запас. Так две тысячи мест стоят столько же, сколько двадцать.
    sealed class Maker(HistoryPage page) : IElementFactory
    {
        // Запас свой: RecyclePool в Windows App SDK 2.7 наружу не отдан.
        readonly Stack<Row> rows = [];
        readonly Stack<TextBlock> days = [];

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var line = (History.Line)args.Data;
            if (line.Page is null)
            {
                var day = days.TryPop(out var spare) ? spare : Caption();
                day.Text = line.Day;
                day.Margin = new Thickness(2, ReferenceEquals(line, page.lines[0]) ? 0 : 14, 0, 6);
                day.Visibility = Visibility.Visible;
                return day;
            }
            var row = rows.TryPop(out var free) ? free : new Row(page);
            row.Show(line);
            row.Visibility = Visibility.Visible;
            return row;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            // Список оставляет отданную строку у себя в дереве. Спрятанной её
            // не найдут ни Tab, ни экранный диктор: иначе они ходят по местам,
            // которых на экране уже нет.
            args.Element.Visibility = Visibility.Collapsed;
            if (args.Element is Row row) rows.Push(row);
            else days.Push((TextBlock)args.Element);
        }

        /// Имя дня над его карточкой. Caption из апстрима.
        static TextBlock Caption()
        {
            var day = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = Palette.Muted };
            AutomationProperties.SetHeadingLevel(day, AutomationHeadingLevel.Level2);
            return day;
        }
    }

    /// Одно место: заголовок, откуда он и когда. Нажатие открывает; под
    /// указателем время сменяется на «Remove». Row из апстрима.
    ///
    /// С клавиатуры список — одна остановка Tab, как любой список Windows:
    /// между строками ходят стрелками, Enter открывает, Delete убирает (а если
    /// места выбраны — их). Ctrl+Enter и Ctrl+пробел выбирают, как Ctrl+щелчок.
    ///
    /// Строка служит разным местам по очереди (см. Maker), поэтому всё, что в
    /// ней написано, переписывает Show.
    sealed class Row : Grid
    {
        readonly HistoryPage page;
        readonly Mark mark = new(16);
        // Выбранное место: галочка на месте значка сайта.
        readonly Icon tick = Glyph(Ui.Check, 16, Palette.Ink, 2.5);
        readonly TextBlock title, site, time;
        readonly Button go, remove;
        History.Trace? place;
        bool hovering;

        public History.Trace? Place => place;
        /// Имя строки для экранного диктора (и для тест-канала).
        public string Called => AutomationProperties.GetName(go);

        /// Фокус с клавиатуры — на эту строку.
        public void Take() => go.Focus(FocusState.Keyboard);

        public Row(HistoryPage page)
        {
            this.page = page;
            BorderBrush = Palette.Hairline;

            title = new TextBlock { FontSize = 13, Foreground = Palette.Ink, TextTrimming = TextTrimming.CharacterEllipsis };
            site = new TextBlock { FontSize = 11.5, Foreground = Palette.Muted, TextTrimming = TextTrimming.CharacterEllipsis };
            var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(title);
            words.Children.Add(site);
            time = new TextBlock { FontSize = 11.5, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center };

            // Значок и заголовок стоят под значком и подсказкой поля поиска
            // (Ui.Entry: поле 12, значок 14, зазор 8; здесь край 1, поле 10,
            // значок 16, зазор 7) — список выровнен по полю над ним.
            var content = new Grid { Padding = new Thickness(10, 9, 14, 9), ColumnSpacing = 7, Background = Palette.Clear };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SetColumn(words, 1);
            SetColumn(time, 2);
            tick.Visibility = Visibility.Collapsed;
            content.Children.Add(mark);
            content.Children.Add(tick);
            content.Children.Add(words);
            content.Children.Add(time);

            go = Bare(new Button { HorizontalAlignment = HorizontalAlignment.Stretch }, "", content);
            go.Click += (_, _) => { if (place is not null) page.Press(place, Held()); };
            go.KeyDown += (_, press) =>
            {
                if (press.Key != VirtualKey.Delete || place is null) return;
                press.Handled = true;
                page.Forget(page.chosen.Count > 0 ? page.chosen : [place.Key], keyed: true);
            };
            AutomationProperties.SetHelpText(go, "Delete removes it from history. Ctrl selects it, Shift selects up to it");

            // «Remove» лежит поверх времени и виден только под указателем. Tab
            // на нём не останавливается: с клавиатуры строку убирает Delete, а
            // экранному диктору кнопка видна всегда.
            // ponytail: пальцем строку не убрать — у касания нет наведения.
            // Понадобится — меню по долгому нажатию.
            remove = Bare(new Button
            {
                CornerRadius = new CornerRadius(11), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), IsTabStop = false,
            }, "", new Border
            {
                CornerRadius = new CornerRadius(11), Padding = new Thickness(8, 4, 8, 4), Background = Palette.Wash,
                Child = new TextBlock { Text = "Remove", FontSize = 11.5, Foreground = Palette.Danger },
            });
            remove.Click += (_, _) => { if (place is not null) page.Forget([place.Key], keyed: false); };

            PointerEntered += (_, at) =>
            {
                hovering = at.Pointer.PointerDeviceType != PointerDeviceType.Touch;
                Paint();
            };
            PointerExited += (_, _) => Leave();
            PointerCanceled += (_, _) => Leave();
            PointerCaptureLost += (_, _) => Leave();

            Children.Add(go);
            Children.Add(remove);
        }

        public void Show(History.Line line)
        {
            place = line.Page!;
            var from = Address.WithoutWWW(place.Address);
            var label = place.Title.Length > 0 ? place.Title : from;
            mark.Show(place.Url, from.Length > 0 ? from[..1].ToUpperInvariant() : "•");
            title.Text = label;
            site.Text = from;
            // Время — по привычке системы: 14:05 или 2:05 PM.
            time.Text = place.Last.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
            AutomationProperties.SetName(go, place.Title.Length > 0 ? $"{label}, {from}, {time.Text}" : $"{from}, {time.Text}");
            AutomationProperties.SetName(remove, $"Remove {label}");

            // Карточка дня рисуется по строке: углы и нижний край — у крайних,
            // верхний край каждой следующей — черта между строками.
            var (top, bottom) = (line.First ? 11 : 0, line.Last ? 11 : 0);
            CornerRadius = new CornerRadius(top, top, bottom, bottom);
            BorderThickness = new Thickness(1, 1, 1, line.Last ? 1 : 0);
            // Прежнее место могло уйти из-под указателя вместе со строкой.
            Leave();
        }

        void Leave()
        {
            hovering = false;
            Paint();
        }

        public void Paint()
        {
            var picked = place is not null && page.chosen.Contains(place.Key);
            Background = picked ? Palette.Wash : hovering ? Palette.Hover : Palette.Ground;
            mark.Visibility = Seen(!picked);
            tick.Visibility = Seen(picked);
            AutomationProperties.SetItemStatus(go, picked ? "Selected" : "");
            remove.Opacity = hovering ? 1 : 0;
            remove.IsHitTestVisible = hovering;
            time.Opacity = hovering ? 0 : 1;
        }
    }
}
