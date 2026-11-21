using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Browser;

/// Страница закладок (BookmarksPanel из Bookmarks.swift; в апстриме —
/// своя страница во вкладке): поиск по имени и адресу, сайты карточками по
/// папкам. Щелчок открывает сайт в новой вкладке, крестик и Delete убирают.
// ponytail: список собирается заново на каждую смену и букву поиска. Для
// сотен закладок незаметно; тысячи из импорта — ItemsRepeater, как в истории.
sealed class BookmarksPage : Grid
{
    readonly Tabs tabs;
    readonly StackPanel list = new() { Spacing = 22 };
    readonly TextBox hunt;

    public BookmarksPage(Tabs tabs)
    {
        this.tabs = tabs;
        Background = Palette.Ground;
        var column = new StackPanel { MaxWidth = 600, Padding = new Thickness(24, 26, 24, 24), Spacing = 16 };
        var (frame, box) = Ui.Entry("Search bookmarks", "Search bookmarks", Ui.Find);
        hunt = box;
        hunt.TextChanged += (_, _) => Fill();
        column.Children.Add(Ui.Heading(Ui.Bookmark, "Bookmarks"));
        column.Children.Add(frame);
        column.Children.Add(list);
        var scroll = new ScrollViewer
        {
            Content = column, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled,
        };
        // Колонке с MaxWidth в прокрутке ширину задаём сами, иначе она не по середине.
        scroll.SizeChanged += (_, _) => column.Width = Math.Min(600, scroll.ActualWidth);
        Children.Add(scroll);
        // Слушаем, только пока страница во вкладке: закрытую не держим в памяти.
        Loaded += (_, _) =>
        {
            Bookmarks.Changed += Fill;
            Fill();
        };
        Unloaded += (_, _) => Bookmarks.Changed -= Fill;
    }

    void Fill()
    {
        list.Children.Clear();
        var found = Bookmarks.Matches(hunt.Text);
        if (found.Count == 0)
        {
            var key = Shortcuts.Key("bookmarks.add") is { } combo ? $", or {combo.Display}," : "";
            list.Children.Add(new TextBlock
            {
                FontSize = 12, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 0),
                Text = Bookmarks.Sites().Count == 0 ? $"No bookmarks yet. The bookmark button at the top{key} keeps the page you’re on" : "No bookmarks match",
            });
            return;
        }
        // Сайты верхнего уровня — первой карточкой без заголовка (заголовок
        // страницы уже «Bookmarks»), под ней папки, каждая со своим путём.
        foreach (var folder in found.GroupBy(site => string.Join(" › ", site.Path)).OrderBy(folder => folder.Key.Length == 0 ? 0 : 1))
        {
            var top = folder.Key.Length == 0;
            var group = SettingsPage.Group(top ? "Bookmarks" : folder.Key, top ? Ui.Bookmark : Ui.FolderOpen, [.. folder.Select(site => Line(site.Node))]);
            if (top) group.Children[0].Visibility = Visibility.Collapsed;
            list.Children.Add(group);
        }
    }

    StackPanel Line(Bookmark node)
    {
        var url = Uri.TryCreate(node.Url, UriKind.Absolute, out var parsed) ? parsed : null;
        var from = url is null ? node.Url ?? "" : Address.Pretty(url);
        var host = url is null ? "" : Address.WithoutWWW(url.Host);
        var mark = new Mark(16);
        mark.Show(url, host.Length > 0 ? host[..1].ToUpperInvariant() : "•");
        var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = node.Label, FontSize = 13, Foreground = Palette.Ink, TextTrimming = TextTrimming.CharacterEllipsis });
        words.Children.Add(new TextBlock { Text = from, FontSize = 11.5, Foreground = Palette.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        var remove = Ui.Pill($"Remove {node.Label}", Ui.Glyph(Ui.Cross, 11, Palette.Muted), () => Bookmarks.Remove(node.Id));
        remove.VerticalAlignment = VerticalAlignment.Center;

        var content = new Grid { Padding = new Thickness(14, 9, 10, 9), ColumnSpacing = 12, Background = Palette.Clear };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mark.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(words, 1);
        SetColumn(remove, 2);
        content.Children.Add(mark);
        content.Children.Add(words);
        content.Children.Add(remove);

        var go = Ui.Bare(new Button { HorizontalAlignment = HorizontalAlignment.Stretch }, $"{node.Label}, {from}", content);
        AutomationProperties.SetHelpText(go, "Delete removes the bookmark");
        go.Click += (_, _) => { if (url is not null) tabs.Open(url); };
        go.KeyDown += (_, press) =>
        {
            if (press.Key != VirtualKey.Delete) return;
            press.Handled = true;
            Bookmarks.Remove(node.Id);
        };
        go.PointerEntered += (_, _) => content.Background = Palette.Wash;
        go.PointerExited += (_, _) => content.Background = Palette.Clear;

        var line = new StackPanel();
        line.Children.Add(new Border { Height = 1, Background = Palette.Hairline });
        line.Children.Add(go);
        return line;
    }
}
