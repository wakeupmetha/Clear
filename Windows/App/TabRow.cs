using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Browser;

/// Строка вкладки в колонке. Перенос SideRow из Sources/Search/Side.swift:
/// буква сайта, заголовок, крестик под указателем, кольцо, пока грузится.
sealed class TabRow : Grid
{
    public readonly Tab Tab;
    readonly Mark mark = new(Metrics.Mark) { Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBlock title;
    readonly Border cover;
    readonly Button go, shut;
    readonly ProgressRing ring;
    readonly FrameworkElement shy;
    bool live, hovering;

    public TabRow(Tab tab, Action select, Action close, Action pin)
    {
        Tab = tab;
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
        go.Click += (_, _) => select();
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
        var pinning = new MenuFlyoutItem();
        pinning.Click += (_, _) => pin();
        var menu = new MenuFlyout();
        menu.Items.Add(pinning);
        menu.Opening += (_, _) =>
        {
            pinning.Text = Tab.Pin is null ? "Pin Tab" : "Unpin Tab";
            pinning.IsEnabled = Tab.Pin is not null || (!Tab.IsBlank && !Tab.IsOwn && !Tab.Private);
        };
        go.ContextFlyout = menu;
        Children.Add(go);

        // Крестик только под указателем. Заголовок при этом не укорачивается,
        // а уходит под крестик: его конец не прыгает на каждой строке, по
        // которой проходит указатель.
        var disc = new Border
        {
            Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Background = Palette.InkDisc,
            Child = Ui.Glyph(Ui.Cross, 12, Palette.Muted, 2.5),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        };
        // Берёт нажатия вся ширина под крестиком, а не только кружок.
        cover = new Border { Width = 30, HorizontalAlignment = HorizontalAlignment.Right, Child = disc };
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

        tab.Changed += Paint;
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
        var ground = live ? Palette.SideWash : hovering ? Palette.SideHover : Palette.Clear;
        Background = ground;
        title.Text = Tab.Label;
        title.Foreground = live ? Palette.Ink : hovering ? Palette.InkSoft : Palette.Muted;
        title.Margin = new Thickness(0, 0, Tab.Loading || Tab.Private ? 28 : 10, 0);
        mark.Show(Tab.Page, Tab.Monogram);
        // У пустой вкладки буквы нет: заголовок начинается от края.
        mark.Visibility = Tab.IsBlank ? Visibility.Collapsed : Visibility.Visible;
        cover.Background = ground;
        // У закреплённой крестика нет: её не закрывают, а кладут (Delete, Ctrl+W).
        shut.Visibility = hovering && Tab.Pin is null ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(go, Tab.Pin is null ? "Delete closes the tab" : "Pinned. Delete puts the tab down");
        ring.IsActive = Tab.Loading;
        ring.Visibility = Tab.Loading && !hovering ? Visibility.Visible : Visibility.Collapsed;
        shy.Visibility = Tab.Private && !Tab.Loading && !hovering ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(go, Tab.Label);
        AutomationProperties.SetName(shut, $"Close {Tab.Label}");
    }
}
