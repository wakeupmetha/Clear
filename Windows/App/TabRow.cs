using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Browser;

/// Строка вкладки в колонке. Перенос SideRow из Sources/Search/Side.swift:
/// буква сайта, заголовок, крестик под указателем, кольцо, пока грузится,
/// динамик, пока играет звук или вкладка заглушена.
sealed class TabRow : Grid
{
    public readonly Tab Tab;
    readonly Mark mark = new(Metrics.Mark) { Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBlock title;
    readonly Border cover, disc;
    readonly Button go, shut, speaker;
    readonly Icon sound;
    readonly ProgressRing ring;
    readonly FrameworkElement shy;
    readonly TranslateTransform lift = new();
    bool live, hovering, held;
    // Перетаскивание: где нажали (по высоте окна), сколько протащили, несут ли.
    double? pressedAt;
    double travel;
    bool carried;

    public TabRow(Tab tab, Tabs tabs, Action select, Action close, Action pin, Action mute, Action duplicate, Action copy,
        Action<double, bool> carry)
    {
        Tab = tab;
        RenderTransform = lift;
        Height = Metrics.Row;
        CornerRadius = new CornerRadius(Metrics.RowRadius);

        // Содержимое строки — кнопка на всю строку: фокус с клавиатуры, Enter
        // выбирает вкладку, Delete закрывает. Крестик лежит поверх неё справа.
        var face = new Grid { Padding = new Thickness(10, 0, 0, 0), Background = Palette.Clear };
        face.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        face.ColumnDefinitions.Add(new ColumnDefinition());

        face.Children.Add(mark);

        title = new TextBlock
        {
            FontSize = Metrics.RowTitle, TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);
        face.Children.Add(title);

        ring = new ProgressRing
        {
            Width = 13, Height = 13, MinWidth = 13, MinHeight = 13, Foreground = Palette.Muted,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0), IsHitTestVisible = false,
        };
        Grid.SetColumn(ring, 1);
        face.Children.Add(ring);

        // Приватная — тихий знак справа, как eye.slash в апстриме.
        shy = Ui.Glyph(Ui.EyeOff, 12, Palette.Muted);
        shy.HorizontalAlignment = HorizontalAlignment.Right;
        shy.VerticalAlignment = VerticalAlignment.Center;
        shy.Margin = new Thickness(0, 0, 9, 0);
        shy.IsHitTestVisible = false;
        Grid.SetColumn(shy, 1);
        face.Children.Add(shy);

        go = Ui.Bare(new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(Metrics.RowRadius), AllowFocusOnInteraction = false,
        }, "", face);
        // Строку, которую протащили, отпускание не выбирает: кнопка зовёт
        // Click раньше, чем отпускание дойдёт до строки.
        go.Click += (_, _) => { if (!carried) select(); };
        go.KeyDown += (_, press) =>
        {
            if (press.Key != VirtualKey.Delete) return;
            press.Handled = true;
            close();
        };
        // Диктор читает «вкладка», а не «кнопка»: пустая вкладка зовётся так же,
        // как строка «New tab» под ней.
        // ponytail: какая вкладка живая, диктор не слышит (её имя — в заголовке
        // окна). Понадобится — свой AutomationPeer с выбором.
        AutomationProperties.SetLocalizedControlType(go, "tab");
        // Правый щелчок и клавиша меню: закрепить или открепить.
        // Там же, как в меню вкладки апстрима (TabBar.swift): дублировать,
        // скопировать адрес, выключить звук (так до него достаёт и клавиатура),
        // и раздел закрытия: эту, другие, ниже, вернуть закрытую.
        var pinning = new MenuFlyoutItem();
        pinning.Click += (_, _) => pin();
        var duplicating = new MenuFlyoutItem { Text = "Duplicate" };
        duplicating.Click += (_, _) => duplicate();
        var copying = new MenuFlyoutItem { Text = "Copy Address" };
        copying.Click += (_, _) => copy();
        var muting = new MenuFlyoutItem();
        muting.Click += (_, _) => mute();
        var closing = new MenuFlyoutItem { Text = "Close Tab" };
        closing.Click += (_, _) => close();
        var others = new MenuFlyoutItem { Text = "Close Other Tabs" };
        others.Click += (_, _) => tabs.CloseOthers(Tab);
        var below = new MenuFlyoutItem { Text = "Close Tabs Below" };
        below.Click += (_, _) => tabs.CloseBelow(Tab);
        var reopening = new MenuFlyoutItem { Text = "Reopen Closed Tab" };
        reopening.Click += (_, _) => tabs.Reopen();
        var menu = new MenuFlyout();
        foreach (var item in new MenuFlyoutItemBase[] { pinning, new MenuFlyoutSeparator(), duplicating, copying, muting, new MenuFlyoutSeparator(), closing, others, below, reopening })
            menu.Items.Add(item);
        menu.Opening += (_, _) =>
        {
            pinning.Text = Tab.Pin is null ? "Pin Tab" : "Unpin Tab";
            pinning.IsEnabled = Tab.Pin is not null || (!Tab.IsBlank && !Tab.IsOwn && !Tab.Private);
            duplicating.IsEnabled = copying.IsEnabled = !Tab.IsBlank && !Tab.IsOwn;
            muting.Text = Tab.Muted ? "Unmute Tab" : "Mute Tab";
            muting.IsEnabled = !Tab.IsBlank && !Tab.IsOwn;
            others.IsEnabled = tabs.All.Count > 1;
            below.IsEnabled = tabs.Below(Tab).Count > 0;
            reopening.IsEnabled = tabs.CanReopen;
        };
        go.ContextFlyout = menu;
        Children.Add(go);

        // Динамик (Speaker апстрима): нажатие выключает звук или возвращает.
        // Под указателем он отступает на место левее крестика, а не прячется
        // под ним, как кольцо. Кружок под ним — только под самим указателем.
        // Tab на нём не останавливается: с клавиатуры звук выключает меню строки.
        sound = Ui.Glyph(Ui.Volume, 11, Palette.Muted, 2.5);
        disc = new Border
        {
            Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Background = Palette.Clear, Child = sound,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        speaker = Ui.Bare(new Button
        {
            Width = 23, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch,
            IsTabStop = false, AllowFocusOnInteraction = false,
        }, "", new Grid { Background = Palette.Clear, Children = { disc } });
        speaker.Click += (_, _) => mute();
        speaker.PointerEntered += (_, _) => disc.Background = Palette.InkDisc;
        speaker.PointerExited += (_, _) => disc.Background = Palette.Clear;
        Children.Add(speaker);

        // Крестик только под указателем. Заголовок при этом не укорачивается,
        // а уходит под крестик: его конец не прыгает на каждой строке, по
        // которой проходит указатель.
        var cross = new Border
        {
            Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Background = Palette.InkDisc,
            Child = Ui.Glyph(Ui.Cross, 12, Palette.Muted, 2.5),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        };
        // Берёт нажатия вся ширина под крестиком, а не только кружок.
        cover = new Border { Width = 30, HorizontalAlignment = HorizontalAlignment.Right, Child = cross };
        // Tab на крестике не останавливается: закрыть вкладку с клавиатуры
        // можно Delete на самой строке; крестик нужен указателю и диктору.
        shut = Ui.Bare(new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch,
            IsTabStop = false, AllowFocusOnInteraction = false,
        }, "", cover);
        shut.Click += (_, _) => close();
        Children.Add(shut);

        PointerEntered += (_, _) => { hovering = true; Paint(); };
        PointerExited += (_, _) => { hovering = false; Paint(); };
        // Средняя кнопка закрывает, как во всех браузерах. Идёт через AddHandler,
        // чтобы видеть нажатие, которое кнопка уже пометила обработанным.
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, press) =>
        {
            if (!press.GetCurrentPoint(this).Properties.IsMiddleButtonPressed) return;
            press.Handled = true;
            close();
        }), handledEventsToo: true);

        // Строку тянут вверх и вниз (Carried апстрима): с левой кнопкой, дальше
        // 5 точек (minimumDistance там же). Нажатие забирает кнопка строки,
        // поэтому события — с handledEventsToo. Куда она встанет, решает окно (carry).
        // ponytail: Escape перенос не отменяет — отпустить на прежнем месте.
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, press) =>
        {
            carried = false;
            pressedAt = press.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? press.GetCurrentPoint(null).Position.Y : null;
        }), handledEventsToo: true);
        AddHandler(PointerMovedEvent, new PointerEventHandler((_, moved) =>
        {
            if (pressedAt is not { } at) return;
            travel = moved.GetCurrentPoint(null).Position.Y - at;
            if (!carried && Math.Abs(travel) <= 5) return;
            carried = true;
            carry(travel, false);
        }), handledEventsToo: true);
        void Put()
        {
            if (pressedAt is null) return;
            pressedAt = null;
            if (carried) carry(travel, true);
        }
        AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => Put()), handledEventsToo: true);
        // Захват ушёл посреди переноса (Alt+Tab, окно свернули): строка встаёт, где её оставили.
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => Put()), handledEventsToo: true);

        tab.Changed += Paint;
        Paint();
    }

    /// Сдвиг строки, пока строку несут (MainWindow.Carry): несомая идёт за
    /// указателем поверх соседей, на подложке; соседи уступают ей место.
    public void Shift(double y, bool held)
    {
        lift.Y = y;
        Canvas.SetZIndex(this, held ? 1 : 0);
        if (this.held == held) return;
        this.held = held;
        Paint();
    }

    public bool Live
    {
        set
        {
            if (live == value) return;
            live = value;
            Paint();
        }
    }

    /// Фокус с клавиатуры стоит на этой строке.
    public bool Focused => go.FocusState != FocusState.Unfocused;

    /// Принять фокус: соседнюю строку закрыли прямо под ним.
    public void Take() => go.Focus(FocusState.Keyboard);

    /// Строка уходит из колонки: вкладка её больше не держит.
    public void Detach() => Tab.Changed -= Paint;

    void Paint()
    {
        var ground = live ? Palette.SideWash : hovering || held ? Palette.SideHover : Palette.Clear;
        Background = ground;
        title.Text = Tab.Label;
        title.Foreground = live ? Palette.Ink : hovering ? Palette.InkSoft : Palette.Muted;
        // Динамик держит конец строки, пока страница играет или заглушена.
        var noise = !Tab.Loading && (Tab.Noisy || Tab.Muted);
        // Под указателем у незакреплённой конец строки берёт крестик.
        var aside = hovering && Tab.Pin is null;
        title.Margin = new Thickness(0, 0, noise && aside ? 51 : noise || Tab.Loading || Tab.Private ? 28 : 10, 0);
        mark.Show(Tab.Page, Tab.Monogram);
        // У пустой вкладки буквы нет: заголовок начинается от края.
        mark.Visibility = Tab.IsBlank ? Visibility.Collapsed : Visibility.Visible;
        cover.Background = ground;
        // У закреплённой крестика нет: её не закрывают, а кладут (Delete, Ctrl+W).
        shut.Visibility = hovering && Tab.Pin is null ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(go, Tab.Pin is null ? "Delete closes the tab" : "Pinned. Delete puts the tab down");
        ring.IsActive = Tab.Loading;
        ring.Visibility = Tab.Loading && !hovering ? Visibility.Visible : Visibility.Collapsed;
        shy.Visibility = Tab.Private && !Tab.Loading && !noise && !hovering ? Visibility.Visible : Visibility.Collapsed;
        speaker.Visibility = noise ? Visibility.Visible : Visibility.Collapsed;
        speaker.Margin = new Thickness(0, 0, aside ? 26 : 3, 0);
        Ui.Draw(sound, Tab.Muted ? Ui.VolumeOff : Ui.Volume);
        ToolTipService.SetToolTip(speaker, Tab.Muted ? "Unmute Tab" : "Mute Tab");
        AutomationProperties.SetName(speaker, $"{(Tab.Muted ? "Unmute" : "Mute")} {Tab.Label}");
        AutomationProperties.SetName(go, Tab.Label);
        AutomationProperties.SetName(shut, $"Close {Tab.Label}");
    }
}
