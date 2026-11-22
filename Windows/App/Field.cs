using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace Browser;

/// Одно поле: адрес или слова. Перенос Omnibox из Sources/Search/Omnibox.swift.
///
/// Поле всплывает над страницей и уходит, когда не нужно: панели инструментов
/// у окна нет. Под ним список мест из истории, после каретки серым —
/// дописывание лучшего совпадения. Ничего из набранного никуда не уходит,
/// пока не нажат Enter.
sealed partial class Field : Grid
{
    readonly Tabs tabs;
    readonly TextBox box, placeholder, ghost;
    readonly TextBlock ruler;
    readonly Border frame, list;
    readonly StackPanel offers = new();

    List<Suggestion> options = [];
    /// Строка списка, выбранная стрелками; -1 — Enter идёт по набранному.
    int picked = -1;
    /// Серое дописывание после каретки.
    string rest = "";
    string last = "";
    /// Следующую смену текста сделали мы сами, а не человек.
    bool quiet;

    public bool IsOpen => Visibility == Visibility.Visible;
    /// Поле убрали, никуда не пойдя.
    public event Action? Dismissed;

    public Field(Tabs tabs)
    {
        this.tabs = tabs;
        Visibility = Visibility.Collapsed;

        var veil = new Border { Background = Palette.Veil };
        veil.Tapped += (_, _) => Dismiss();
        Children.Add(veil);

        // Серое — подсказка в пустом поле и дописывание — тоже поля ввода, только
        // для чтения и невидимые для мыши, клавиатуры и диктора. Буквы рисует
        // тот же движок, что набранные, и они встают на ту же линию той же
        // толщины. TextBlock рядом с полем ввода стоял на точку ниже, и серое
        // выглядело съехавшим и тоньше набранного.
        TextBox Line(Microsoft.UI.Xaml.Media.Brush colour, bool editable = false)
        {
            var line = new TextBox
            {
                FontSize = Metrics.FieldText, Foreground = colour, MinHeight = 0, Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center, IsSpellCheckEnabled = false, Template = Ui.PlainText,
                IsReadOnly = !editable, IsTabStop = editable, IsHitTestVisible = editable,
            };
            if (!editable)
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(line, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            return line;
        }
        placeholder = Line(Palette.InkGhost);
        placeholder.Text = "Enter a web address";
        ghost = Line(Palette.InkGhost);
        // Невидимая линейка: ширина набранного, чтобы поставить серое
        // дописывание сразу после него.
        ruler = new TextBlock { FontSize = Metrics.FieldText, TextWrapping = TextWrapping.NoWrap, Opacity = 0, IsHitTestVisible = false };

        box = Line(Palette.Ink, editable: true);
        // Имя для экранного диктора: он читает его рядом с набранным.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Address");
        box.TextChanged += (_, _) => Typed();
        box.PreviewKeyDown += Key;

        var line = new Grid();
        line.Children.Add(ruler);
        line.Children.Add(placeholder);
        line.Children.Add(ghost);
        line.Children.Add(box);
        frame = new Border
        {
            Height = Metrics.FieldHeight, CornerRadius = new CornerRadius(Metrics.FieldRadius),
            Background = Palette.Ground, BorderBrush = Palette.Hairline, BorderThickness = new Thickness(1),
            Padding = new Thickness(22, 0, 22, 0), Child = line,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        };
        Children.Add(frame);

        // Список висит под полем, а не стоит с ним в одной стопке: растущий
        // список не сдвигает поле из-под набираемого.
        list = new Border
        {
            CornerRadius = new CornerRadius(Metrics.FieldRadius), Background = Palette.Ground,
            BorderBrush = Palette.Hairline, BorderThickness = new Thickness(1), Padding = new Thickness(6),
            Child = offers, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        };
        Children.Add(list);

        // Мягкая тень под полем и списком: они лежат над страницей.
        foreach (var raised in (Border[])[frame, list])
        {
            raised.Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow();
            raised.Translation = new System.Numerics.Vector3(0, 0, 24);
        }

        SizeChanged += (_, _) => Place();
    }

    // Чуть выше середины: ровно по центру читается как «низко».
    void Place()
    {
        var width = Math.Min(Metrics.FieldWidth, Math.Max(0, ActualWidth - 28));
        var top = Math.Max(14, (ActualHeight - Metrics.FieldHeight) / 2 - 30);
        frame.Width = list.Width = width;
        frame.Margin = new Thickness(0, top, 0, 0);
        list.Margin = new Thickness(0, top + Metrics.FieldHeight + 8, 0, 0);
    }

    /// Показывает поле с этим текстом, выделенным целиком: набранное его заменит.
    public void Open(string text)
    {
        Visibility = Visibility.Visible;
        Place();
        Set(text);
        // Фокус сразу после показа не берётся: элемент ещё не размещён.
        DispatcherQueue.TryEnqueue(() =>
        {
            box.Focus(FocusState.Programmatic);
            // Выделяем только то, с чем поле открыли: если за это время в
            // него уже набрали, каретка остаётся в конце набранного.
            if (box.Text == text) box.SelectAll();
        });
    }

    /// Как будто набрали: для проверок через тест-канал.
    public void Type(string text)
    {
        if (!IsOpen) Open("");
        last = "";
        box.Text = text;
        box.SelectionStart = text.Length;
    }

    public void Close() => Visibility = Visibility.Collapsed;

    /// Фокус в поле, ничего не меняя в набранном.
    public void Take() => box.Focus(FocusState.Programmatic);

    /// После смены темы. Всё остальное перекрашивается само, вместе с кистью
    /// (см. Palette), но поле ввода запоминает цвет текста в момент, когда ему
    /// дали кисть, и за её цветом не следит: кисть надо дать заново.
    public void Retint()
    {
        box.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Palette.Ink.Color);
        placeholder.Foreground = ghost.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Palette.InkGhost.Color);
    }

    void Dismiss()
    {
        Close();
        Dismissed?.Invoke();
    }

    // Текст без подсказок: поле открыли, ещё ничего не набрав.
    void Set(string text)
    {
        if (box.Text != text)
        {
            quiet = true;
            box.Text = text;
        }
        last = text;
        options = [];
        picked = -1;
        rest = "";
        placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Ghost();
        Offers();
    }

    void Typed()
    {
        if (quiet)
        {
            quiet = false;
            return;
        }
        var text = box.Text;
        // Стёрли букву — не дописываем её обратно.
        var erasing = text.Length < last.Length;
        last = text;
        placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        options = tabs.History.Suggestions(text);
        picked = -1;
        rest = !erasing && box.SelectionStart == text.Length ? History.Completion(text, options) ?? "" : "";
        Ghost();
        Offers();
    }

    void Ghost()
    {
        ruler.Text = box.Text;
        ruler.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ghost.Text = rest;
        ghost.Margin = new Thickness(ruler.DesiredSize.Width, 0, 0, 0);
    }

    void Key(object sender, KeyRoutedEventArgs press)
    {
        switch (press.Key)
        {
            case VirtualKey.Enter:
                Submit();
                break;
            case VirtualKey.Escape:
                Dismiss();
                break;
            case VirtualKey.Down when options.Count > 0:
                picked = Math.Min(picked + 1, options.Count - 1);
                Offers();
                break;
            case VirtualKey.Up when options.Count > 0:
                picked = Math.Max(picked - 1, -1);
                Offers();
                break;
            // Принять дописывание: оно становится набранным.
            case VirtualKey.Right or VirtualKey.Tab when rest.Length > 0 && box.SelectionStart == box.Text.Length:
                Type(box.Text + rest);
                break;
            default:
                return;
        }
        press.Handled = true;
    }

    void Submit()
    {
        var destination = picked >= 0 ? options[picked].Url : Tabs.Destination(box.Text + rest);
        if (destination is null) return;   // пустое поле никуда не ведёт
        Close();
        tabs.Go(destination);
    }

    void Offers()
    {
        offers.Children.Clear();
        for (var i = 0; i < options.Count; i++) offers.Children.Add(Offer(options[i], i == picked));
        list.Visibility = options.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    Grid Offer(Suggestion option, bool chosen)
    {
        var row = new Grid
        {
            Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(Metrics.RowRadius),
            Background = chosen ? Palette.Wash : Palette.Clear, ColumnSpacing = 10,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());

        var mark = new Mark(14);
        mark.Show(option.Url, Address.WithoutWWW(option.Url.Host) is { Length: > 0 } host ? host[..1].ToUpperInvariant() : "");
        row.Children.Add(mark);

        // Без заголовка место называется своим адресом. Заголовок и адрес разного
        // размера прижаты к низу: так их базовые линии расходятся на четверть
        // точки (разница высоты хвостов), а по середине расходились на точку.
        var named = option.Title.Length > 0;
        var name = new TextBlock
        {
            Text = named ? option.Title : option.Key, FontSize = 13, Foreground = Palette.Ink, MaxWidth = 300,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        SetColumn(name, 1);
        row.Children.Add(name);
        if (named)
        {
            var address = new TextBlock
            {
                Text = option.Key, FontSize = 12, Foreground = Palette.Muted,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            SetColumn(address, 2);
            row.Children.Add(address);
        }

        if (!chosen)
        {
            row.PointerEntered += (_, _) => row.Background = Palette.Hover;
            row.PointerExited += (_, _) => row.Background = Palette.Clear;
        }
        row.Tapped += (_, _) =>
        {
            Close();
            tabs.Go(option.Url);
        };
        return row;
    }
}
