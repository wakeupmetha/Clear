using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Browser;

// Колонка вкладок, убранная по Ctrl+S. Перенос Sources/Search/Fold.swift.
//
// Колонка — это двести с лишним точек, которые страница не получает назад,
// даже когда вы только читаете. Сложенная, она отдаёт странице всё окно и
// возвращается по Ctrl+S или первой кнопке полосы.
//
// От левого края окна колонка выезжает только в режиме фокуса (Focus), где
// полосы с кнопкой нет, и сложенная во весь экран (F11). Сложенная в обычном
// окне — нет (решение владельца 2026-11-22, в апстриме выезжает): выезд от края
// ловил руку, которая шла к странице, — лишнее движение. Выехавшая уходит, когда указатель её покидает,
// и не сразу: рука, промахнувшаяся на обратном пути, не должна её терять.
//
// Сложена колонка или нет, запоминается и возвращается при следующем запуске.
// Ctrl+S, когда колонка выехала поверх страницы, оставляет её на месте, уже
// рядом со страницей.
sealed partial class MainWindow
{
    readonly ColumnDefinition side = new() { Width = new GridLength(Metrics.Side) };
    readonly TranslateTransform slide = new();
    // Панель лежит поверх окна и занимает то же место в обоих случаях: рядом со
    // страницей под неё отведён столбец, поверх страницы — нет.
    readonly Border panel = new()
    {
        Width = Metrics.Side, HorizontalAlignment = HorizontalAlignment.Left,
        Background = Palette.Frame, BorderBrush = Palette.Hairline, Shadow = new ThemeShadow(),
    };
    // Край окна, который зовёт колонку: полоска шириной в отступ карточки.
    readonly Border edge = new()
    {
        Width = Metrics.CardGap, HorizontalAlignment = HorizontalAlignment.Left, Background = Palette.Clear,
    };
    readonly DispatcherTimer leaving = new() { Interval = TimeSpan.FromMilliseconds(350) };
    static readonly Windows.UI.ViewManagement.UISettings Desktop = new();
    Storyboard? moving;
    bool folded, revealed;

    /// Собирает колонку: панель со строками и край, который её зовёт.
    void Folding(UIElement column)
    {
        panel.Child = column;
        panel.RenderTransform = slide;
        Grid.SetRow(edge, 1);
        Grid.SetColumnSpan(edge, 2);
        Grid.SetRow(panel, 1);
        Grid.SetColumnSpan(panel, 2);
        root.Children.Add(edge);
        root.Children.Add(panel);

        edge.PointerEntered += (_, _) => Reveal();
        panel.PointerEntered += (_, _) => leaving.Stop();
        panel.PointerExited += (_, _) => { if (revealed) leaving.Start(); };
        leaving.Tick += (_, _) =>
        {
            leaving.Stop();
            Conceal();
        };

        folded = Settings.Bool("sidebar.folded");
        Arrange();
    }

    /// Ctrl+S: колонку с глаз долой или обратно.
    void ToggleFold()
    {
        leaving.Stop();
        if (folded)
        {
            // Уже выехала поверх страницы — остаётся где была, теперь рядом с ней.
            var over = revealed;
            folded = revealed = false;
            Arrange();
            if (!over) Slide(-Metrics.Side, 0, 160);
        }
        else
        {
            folded = true;
            // Страница занимает место колонки, когда та уже ушла.
            Slide(0, -Metrics.Side, 130, Arrange);
        }
        Settings.Set("sidebar.folded", folded);
    }

    /// В режиме фокуса и сложенная во весь экран колонка выезжает поверх страницы.
    void Reveal()
    {
        if (immersed || !(focused || (full && folded)) || revealed) return;
        revealed = true;
        Arrange();
        Slide(-Metrics.Side, 0, 160);
    }

    void Conceal()
    {
        if (!revealed) return;
        revealed = false;
        Slide(0, -Metrics.Side, 130, Arrange);
    }

    /// Раскладка по нынешнему состоянию, без движения.
    void Arrange()
    {
        var docked = !folded && !focused && !immersed;
        side.Width = new GridLength(docked ? Metrics.Side : 0);
        // Сложена — страница берёт всё окно и отступает от левого края так же,
        // как от остальных; в режиме фокуса — и от верхнего. Во весь экран —
        // без отступов, углов и края.
        card.Margin = immersed ? new Thickness(0)
            : new Thickness(docked ? 0 : Metrics.CardGap, focused ? Metrics.CardGap : 0, Metrics.CardGap, Metrics.CardGap);
        card.CornerRadius = new CornerRadius(immersed ? 0 : Metrics.CardRadius);
        card.BorderThickness = new Thickness(immersed ? 0 : 1);
        panel.Visibility = docked || revealed ? Visibility.Visible : Visibility.Collapsed;
        // Поверх страницы колонка лежит на своём фоне: с краем и тенью.
        panel.BorderThickness = new Thickness(0, 0, revealed ? 1 : 0, 0);
        panel.Translation = new Vector3(0, 0, revealed ? 32 : 0);
        Ui.Draw((Icon)((Border)fold.Content).Child, folded ? Ui.OpenPane : Ui.ClosePane);
        AutomationProperties.SetName(fold, folded ? "Show sidebar" : "Hide sidebar");
    }

    // Движение идёт сдвигом панели, а не её шириной: страница при этом не
    // перекладывается на каждом кадре. Если в системе выключены эффекты
    // анимации, колонка просто оказывается на месте.
    void Slide(double from, double to, double milliseconds, Action? then = null)
    {
        moving?.Stop();
        moving = null;
        if (!Desktop.AnimationsEnabled)
        {
            slide.X = to;
            then?.Invoke();
            return;
        }
        slide.X = from;
        var move = new DoubleAnimation
        {
            From = from, To = to, Duration = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = new CubicEase { EasingMode = to == 0 ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        Storyboard.SetTarget(move, slide);
        Storyboard.SetTargetProperty(move, "X");
        var story = new Storyboard();
        story.Children.Add(move);
        story.Completed += (_, _) =>
        {
            if (moving != story) return;   // её уже сменило другое движение
            moving = null;
            story.Stop();
            slide.X = to;
            then?.Invoke();
        };
        moving = story;
        story.Begin();
    }
}
