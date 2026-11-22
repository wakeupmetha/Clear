using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Browser;

/// На странице расширения в Chrome Web Store — плашка внизу страницы: добавить
/// его в браузер. Кнопка самого магазина там говорит «Switch to Chrome».
/// StoreOffer из Sources/Search/ExtensionsUI.swift.
///
/// Какое расширение — только из адреса вкладки (Crx.StoreId), а не из того, что
/// говорит страница; «Add» нажимает только человек: страница до этой кнопки не
/// достаёт. Ставится тем же путём, что из настроек (Addons.InstallFromStore).
// ponytail: серая кнопка магазина и баннер «Switch to Chrome» остаются как есть.
// В апстриме скрипт в странице (StoreRelay.swift) ставит вместо них «Add to
// Search». Понадобится — перенести его; плашка останется на случай, когда
// магазин сменит разметку.
sealed class StoreOffer
{
    public readonly Border View;
    readonly Tabs tabs;
    readonly TextBlock words = new() { FontSize = 12.5, Foreground = Palette.Ink, VerticalAlignment = VerticalAlignment.Center };
    readonly Button add;
    // Расширения, которые ставятся сейчас: уйти со страницы и вернуться можно.
    readonly HashSet<string> busy = [];
    string? id;

    public StoreOffer(Tabs tabs)
    {
        this.tabs = tabs;
        var word = Ui.Word("Add");
        word.Foreground = Palette.Ground;
        add = Ui.Bare(new Button { CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Center }, $"Add this extension to {Store.Name}",
            new Border { Height = 26, CornerRadius = new CornerRadius(13), Padding = new Thickness(11, 0, 11, 2), Background = Palette.Ink, Child = word });
        add.Click += (_, _) => Add();
        var puzzle = Ui.Glyph(Ui.Puzzle, 13, Palette.Muted);
        puzzle.VerticalAlignment = VerticalAlignment.Center;
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        line.Children.Add(puzzle);
        line.Children.Add(words);
        line.Children.Add(add);
        View = new Border
        {
            CornerRadius = new CornerRadius(22), Padding = new Thickness(16, 9, 10, 9), Child = line,
            Background = Palette.Ground, BorderBrush = Palette.Hairline, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 30), Shadow = new ThemeShadow(), Translation = new Vector3(0, 0, 32),
            Visibility = Visibility.Collapsed,
        };
        // Поставили (отсюда, из настроек) или убрали — плашка пропадает или возвращается.
        Addons.Changed += () => Show(tabs.Live);
    }

    /// Привести к живой вкладке: есть ли что предложить.
    public void Show(Tab? live)
    {
        id = Crx.StoreId(live?.Page);
        var added = id is not null && Extensions.All().Any(record => record.Source == Addons.StorePage(id));
        View.Visibility = id is null || added ? Visibility.Collapsed : Visibility.Visible;
        if (id is null || added) return;
        var adding = busy.Contains(id);
        words.Text = adding ? "Adding…" : $"Add this extension to {Store.Name}";
        words.Foreground = Palette.Ink;
        add.Visibility = adding ? Visibility.Collapsed : Visibility.Visible;
    }

    async void Add()
    {
        if (id is not { } adding || !busy.Add(adding)) return;
        Show(tabs.Live);
        string? trouble = null;
        try { await tabs.WithProfile(profile => Addons.InstallFromStore(profile, adding)); }
        catch (Exception e) when (e is COMException or InvalidDataException or IOException or UnauthorizedAccessException
            or ArgumentException or HttpRequestException or TaskCanceledException)
        {
            trouble = e.Message;
        }
        busy.Remove(adding);
        Show(tabs.Live);
        // Ошибка — на той же плашке, если человек ещё на этой странице; «Add» снова доступна.
        if (trouble is not null && id == adding)
        {
            words.Text = $"Couldn't add it: {trouble}";
            words.Foreground = Palette.Danger;
        }
    }
}
