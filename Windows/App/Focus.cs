using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Browser;

// Режим фокуса (решение пользователя 2026-11-20: отдельный режим, а не часть
// Ctrl+S). Страница берёт всё окно: прячутся колонка, полоса окна с адресной
// строкой и системные кнопки окна. Колонка выезжает от левого края, как у
// сложенной (Fold), полоса — от верхнего и держится, пока указатель над ней
// или над кнопками окна. Ctrl+Shift+F или переключатель в настройках;
// запоминается («window.focus»). В апстриме такого режима нет.
sealed partial class MainWindow
{
    // Верхний край окна, который зовёт полосу: тот же отступ, что у карточки.
    readonly Border crest = new() { Height = Metrics.CardGap, VerticalAlignment = VerticalAlignment.Top, Background = Palette.Clear };
    // Фон выехавшей полосы: поверх страницы, с краем и тенью, как у выехавшей колонки.
    readonly Border cover = new()
    {
        Background = Palette.Frame, BorderBrush = Palette.Hairline, BorderThickness = new Thickness(0, 0, 0, 1),
        Shadow = new ThemeShadow(), Translation = new Vector3(0, 0, 32),
    };
    // Указатель над кнопками окна событий не даёт (их рисует система), поэтому
    // где он, спрашиваем сами, а не ждём ухода с полосы.
    readonly DispatcherTimer watching = new() { Interval = TimeSpan.FromMilliseconds(350) };
    bool focused, crowned;
    // Страница во весь экран (видео, ContainsFullScreenElement живой вкладки):
    // окно во весь экран, остаётся только страница, без краёв, которые зовут
    // колонку и полосу. Выход — Esc страницы или её кнопка; уход на другую
    // вкладку тоже выводит (Tabs.Select). В апстриме это делает WebKit своим окном.
    bool immersed;
    // Каким окно было до этого: развёрнутое возвращается развёрнутым.
    AppWindowPresenter? kept;

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out PointInt32 point);

    void Focusing()
    {
        Grid.SetColumnSpan(cover, 2);
        Grid.SetColumnSpan(crest, 2);
        Canvas.SetZIndex(cover, 2);
        Canvas.SetZIndex(doors, 3);
        Canvas.SetZIndex(bar.View, 3);
        Canvas.SetZIndex(actions.View, 3);
        Canvas.SetZIndex(crest, 4);
        root.Children.Add(cover);
        root.Children.Add(crest);
        crest.PointerEntered += (_, _) => Crown();
        watching.Tick += (_, _) =>
        {
            // Полоса — верх окна высотой в полосу, вместе с кнопками окна.
            if (!GetCursorPos(out var at)) return;
            var corner = AppWindow.Position;
            var scale = root.XamlRoot?.RasterizationScale ?? 1;
            var over = at.X >= corner.X && at.X < corner.X + AppWindow.Size.Width
                && at.Y >= corner.Y && at.Y < corner.Y + (Metrics.Band + Metrics.CardGap) * scale;
            if (!over) Uncrown();
        };
        focused = Settings.Bool("window.focus");
        Compose();
    }

    void ToggleFocus() => Settings.Set("window.focus", !focused);

    /// Настройку сменили (сочетание, страница настроек, тест-канал).
    void FollowFocus()
    {
        if (Settings.Bool("window.focus") == focused) return;
        focused = !focused;
        watching.Stop();
        crowned = false;
        Conceal();
        Compose();
    }

    /// Указатель у верхнего края: полоса выезжает поверх страницы.
    void Crown()
    {
        if (!focused || crowned || immersed) return;
        crowned = true;
        Compose();
        watching.Start();
    }

    void Uncrown()
    {
        watching.Stop();
        if (!crowned) return;
        crowned = false;
        Compose();
    }

    /// Страница вошла во весь экран или вышла (MainWindow.Sync, по живой вкладке).
    void Immerse(bool on)
    {
        if (immersed == on) return;
        immersed = on;
        // Тихое окно во весь экран не встаёт: закрыло бы экран человеку. В нём
        // проверяется раскладка.
        if (!App.Quiet)
        {
            if (on)
            {
                kept = AppWindow.Presenter;
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            else if (kept is not null) AppWindow.SetPresenter(kept);
            else AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        }
        watching.Stop();
        crowned = false;
        Conceal();
        Compose();
    }

    /// Раскладка по режиму, без движения.
    void Compose()
    {
        var hidden = (focused && !crowned) || immersed;
        var whole = focused || immersed;
        // Страница поднимается на место полосы; выехавшая полоса ложится поверх неё.
        // Колонка и зовущий её край — во всю высоту окна, как страница.
        foreach (var part in new FrameworkElement[] { card, panel, edge })
        {
            Grid.SetRow(part, whole ? 0 : 1);
            Grid.SetRowSpan(part, whole ? 2 : 1);
        }
        doors.Visibility = bar.View.Visibility = actions.View.Visibility = band.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        cover.Visibility = focused && crowned ? Visibility.Visible : Visibility.Collapsed;
        crest.Visibility = hidden && !immersed ? Visibility.Visible : Visibility.Collapsed;
        // Спрятанная полоса не должна таскать окно за верх страницы, а кнопки
        // окна — висеть над ней. Свёрнутая полоса своё место у перетаскивания
        // сама не отдаёт (проверено: остаётся 40 точек), снимаем его; вернувшейся
        // его отдаёт Passthrough на следующей раскладке.
        AppWindow.TitleBar.PreferredHeightOption = hidden ? TitleBarHeightOption.Collapsed : TitleBarHeightOption.Tall;
        if (hidden) InputNonClientPointerSource.GetForWindowId(AppWindow.Id).ClearRegionRects(NonClientRegionKind.Caption);
        holes = [];
        Arrange();
    }
}
