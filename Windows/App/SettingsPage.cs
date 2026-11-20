using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using static Browser.Ui;

namespace Browser;

/// Настройки: своя страница во вкладке (browser://settings), а не окно поверх.
/// Вид перенесён из Sources/Search/Settings.swift: группы с маленьким
/// заголовком, в каждой карточка строк; строка — значок в плитке, что это,
/// одна строка о том, что она делает, и элемент управления справа.
///
/// Разделов слева и поиска по настройкам, как в апстриме, нет: настроек пока
/// шесть, они видны все сразу.
///
/// Страница — только вид на Settings. Элементы пишут туда, а окно, вкладки и
/// сама страница узнают об изменении из Settings.Changed. Поэтому Ctrl+S при
/// открытых настройках двигает и переключатель на странице.
sealed class SettingsPage : Grid
{
    /// Каждый элемент умеет привести себя к тому, что записано в настройках.
    readonly List<Action> follow = [];
    /// Элементы сейчас приводятся к настройкам: это не решение человека,
    /// записывать его обратно не надо.
    bool following;

    public SettingsPage(Tabs tabs)
    {
        Background = Palette.Ground;

        // Узкой держим колонку, а не прокрутку: пустое место по бокам тоже
        // должно прокручивать страницу.
        var column = new StackPanel { MaxWidth = 600, Padding = new Thickness(24, 26, 24, 24), Spacing = 22 };
        column.Children.Add(Heading(Ui.Gear, "Settings"));
        column.Children.Add(Group("Appearance", Ui.Colour,
            Row(Ui.SunMoon, "Appearance", "Light, dark, or as Windows has it. Pages follow too",
                Choice("Appearance", [("light", "Light"), ("dark", "Dark"), ("system", "System")],
                    () => Settings.Text("look") is "light" or "dark" ? Settings.Text("look")! : "system",
                    look => Settings.Set("look", look))),
            Row(Ui.ClosePane, "Fold the tab column", "The page takes the whole window; the tabs come out at the left edge. Ctrl+S",
                Switch("Fold the tab column", () => Settings.Bool("sidebar.folded"), on => Settings.Set("sidebar.folded", on))),
            Row(Ui.Focus, "Focus mode", "Only the page: the tabs come out at the left edge, the top bar at the top edge. Ctrl+Shift+F",
                Switch("Focus mode", () => Settings.Bool("window.focus"), on => Settings.Set("window.focus", on)))));
        column.Children.Add(Group("Search", Ui.Find,
            Row(Ui.TextSearch, "Search with", "Where words that aren't an address go", EnginePicker()),
            CustomEngine()));
        column.Children.Add(Group("Privacy", Ui.Guard,
            Row(Ui.ShieldBan, "Block ads and trackers",
                Shield.Delegated ? "uBlock Origin blocks them, YouTube ads too" : "Third parties whose only job is to watch or to sell",
                Switch("Block ads and trackers", () => Shield.Enabled, on => Shield.Enabled = on)),
            // С uBlock свой список сайтов не действует: доверенные сайты — в его панели.
            Shield.Delegated
                ? Row(Ui.Sliders, "uBlock Origin", "Filter lists, trusted sites, the logger. uBlock Origin 1.76.6, GPLv3",
                    Pill("Open uBlock Origin settings", Word("Open"), () => { if (UBlock.Dashboard is { } dashboard) tabs.Open(dashboard); }))
                : PausedSites()));
        column.Children.Add(Group("Extensions", Ui.Puzzle, ExtensionRows(tabs)));
        column.Children.Add(Group("Engine", Ui.Cpu,
            Row(Ui.Flask, "Engine experiments", "The engine's own flags page, edge://flags. For testing: a flag can break pages",
                Pill("Open engine experiments", Word("Open"), () => tabs.Open(new Uri("edge://flags"))))));

        Children.Add(new ScrollViewer
        {
            Content = column, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        });

        // Слушаем настройки, только пока страница во вкладке: закрытую вкладку
        // они не должны держать в памяти.
        Loaded += (_, _) =>
        {
            Settings.Changed += Changed;
            Follow();
        };
        Unloaded += (_, _) => Settings.Changed -= Changed;
    }

    void Changed(string key) => Follow();

    void Follow()
    {
        following = true;
        foreach (var act in follow) act();
        following = false;
    }

    /// Группа: маленький заголовок со значком и под ним карточка строк.
    /// GroupBlock и Card из апстрима.
    static StackPanel Group(string title, string glyph, params StackPanel[] rows)
    {
        // Первая строка карточки начинается без черты над собой.
        rows[0].Children[0].Visibility = Visibility.Collapsed;
        var lines = new StackPanel();
        foreach (var row in rows) lines.Children.Add(row);

        var name = new TextBlock
        {
            Text = title.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, CharacterSpacing = 50,
            Foreground = Palette.InkSoft, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetHeadingLevel(name, AutomationHeadingLevel.Level2);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(2, 0, 0, 0) };
        heading.Children.Add(Ui.Glyph(glyph, 13, Palette.InkSoft));
        heading.Children.Add(name);

        var block = new StackPanel { Spacing = 8 };
        block.Children.Add(heading);
        block.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(11), Background = Palette.Ground,
            BorderBrush = Palette.Hairline, BorderThickness = new Thickness(1), Child = lines,
        });
        return block;
    }

    /// Одна настройка. Широкий элемент (список, поле) встаёт под словами.
    /// Первым в строке идёт черта, отделяющая её от предыдущей: спрятанная
    /// строка уносит свою черту с собой. SettingRow из апстрима.
    static StackPanel Row(string glyph, string title, string detail, UIElement? control = null, UIElement? under = null)
    {
        var grid = new Grid { Padding = new Thickness(14, 10, 14, 10), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Background = Palette.Wash,
            Child = Ui.Glyph(glyph, 15, Palette.InkSoft), VerticalAlignment = VerticalAlignment.Center,
        });

        var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Palette.Ink, TextWrapping = TextWrapping.Wrap });
        words.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap });
        SetColumn(words, 1);
        grid.Children.Add(words);

        if (control is FrameworkElement beside)
        {
            beside.VerticalAlignment = VerticalAlignment.Center;
            SetColumn(beside, 2);
            grid.Children.Add(beside);
        }
        if (under is FrameworkElement below)
        {
            below.Margin = new Thickness(0, 10, 0, 0);
            SetRow(below, 1);
            SetColumn(below, 1);
            SetColumnSpan(below, 2);
            grid.Children.Add(below);
        }

        var row = new StackPanel();
        row.Children.Add(new Border { Height = 1, Background = Palette.Hairline });
        row.Children.Add(grid);
        return row;
    }

    /// Включено или выключено, чернилами, а не синим. Switch из апстрима.
    /// tracked: false — состояние не из Settings (расширение): читается один раз.
    ToggleButton Switch(string name, Func<bool> read, Action<bool> write, bool tracked = true)
    {
        var knob = new Ellipse { Width = 14, Height = 14, Fill = Palette.Ground, Margin = new Thickness(2) };
        var track = new Border { Width = 30, Height = 18, CornerRadius = new CornerRadius(9), Child = knob };
        var toggle = Ui.Bare(new ToggleButton { CornerRadius = new CornerRadius(9) }, name, track);
        void Paint()
        {
            var on = toggle.IsChecked == true;
            track.Background = on ? Palette.Ink : Palette.Faint;
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
        void Flipped()
        {
            Paint();
            if (!following) write(toggle.IsChecked == true);
        }
        // По смене состояния, а не по нажатию: экранный диктор переключает,
        // не нажимая.
        toggle.Checked += (_, _) => Flipped();
        toggle.Unchecked += (_, _) => Flipped();
        void Read()
        {
            toggle.IsChecked = read();
            Paint();
        }
        if (tracked) follow.Add(Read);
        else
        {
            var was = following;
            following = true;
            Read();
            following = was;
        }
        return toggle;
    }

    /// Несколько вариантов в серой дорожке, выбранный приподнят белым.
    /// Segmented из апстрима.
    Border Choice(string name, (string Value, string Title)[] options, Func<string> read, Action<string> write)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (value, title) in options)
        {
            // Буквы посередине, как в капсуле (Ui.Pill).
            var text = new TextBlock { Text = title, FontSize = 11.5, TextLineBounds = TextLineBounds.Tight, VerticalAlignment = VerticalAlignment.Center };
            var lift = new Border { Height = 26, CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 0, 10, 2), Child = text };
            var option = Ui.Bare(new RadioButton { GroupName = name, CornerRadius = new CornerRadius(7) }, $"{name}: {title}", lift);
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
                if (!following) write(value);
            };
            option.Unchecked += (_, _) => Paint();
            follow.Add(() =>
            {
                option.IsChecked = read() == value;
                Paint();
            });
            line.Children.Add(option);
        }
        return new Border { CornerRadius = new CornerRadius(9), Background = Palette.Wash, Padding = new Thickness(2), Child = line };
    }

    /// Поисковик: капсула с его именем, по нажатию — список.
    Button EnginePicker()
    {
        var word = Word("");
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(word);
        label.Children.Add(Ui.Glyph(Ui.Down, 12, Palette.Muted, 2.5));
        var pill = Pill("Search with", label);

        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach (var engine in Enum.GetValues<Engine>())
        {
            var item = new RadioMenuFlyoutItem { Text = engine == Engine.Custom ? "Custom…" : engine.Title(), GroupName = "engine" };
            item.Click += (_, _) => Settings.Set("search.engine", engine.Key());
            follow.Add(() => item.IsChecked = Engines.Chosen == engine);
            menu.Items.Add(item);
        }
        pill.Flyout = menu;

        follow.Add(() => word.Text = Engines.Chosen == Engine.Custom
            ? Engines.BareHost(Engines.Written(Settings.Text("search.custom") ?? "")) ?? "Custom"
            : Engines.Chosen.Title());
        return pill;
    }

    /// Адрес своего поисковика. Строка есть, только пока выбран «Custom».
    StackPanel CustomEngine()
    {
        var (frame, box) = Entry("Custom search address", "https://example.com/search?q=%s");
        var note = new TextBlock
        {
            Text = $"Until the address has %s in it, {Engines.Standard.Title()} answers",
            FontSize = 11.5, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap,
        };
        var under = new StackPanel { Spacing = 6 };
        under.Children.Add(frame);
        under.Children.Add(note);
        var row = Row(Ui.Globe, "Custom search address", "The words you type go where %s is", under: under);

        // Пишем по мере набора: кнопки «сохранить» у настроек нет.
        box.TextChanged += (_, _) => { if (!following) Settings.Set("search.custom", box.Text); };
        follow.Add(() =>
        {
            var written = Settings.Text("search.custom") ?? "";
            if (box.Text != written) box.Text = written;
            note.Visibility = Engines.Accepts(Engines.Written(written)) ? Visibility.Collapsed : Visibility.Visible;
            row.Visibility = Engines.Chosen == Engine.Custom ? Visibility.Visible : Visibility.Collapsed;
        });
        return row;
    }

    /// Сайты, на которых блокировщик выключен: список и поле, чтобы добавить.
    /// Строка есть, только пока блокировщик включён.
    StackPanel PausedSites()
    {
        var sites = new StackPanel { Spacing = 2 };
        var (frame, box) = Entry("Site to stop blocking on", "example.com");
        void Add()
        {
            // Не сайт — оставляем набранное как есть: человек его поправит.
            if (Shield.Named(box.Text) is not { } site) return;
            box.Text = "";
            Shield.Pause(site, true);
        }
        box.KeyDown += (_, press) =>
        {
            if (press.Key != VirtualKey.Enter) return;
            press.Handled = true;
            Add();
        };
        var adding = new Grid { ColumnSpacing = 8 };
        adding.ColumnDefinitions.Add(new ColumnDefinition());
        adding.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var add = Pill("Add site", Word("Add"), Add);
        SetColumn(add, 1);
        adding.Children.Add(frame);
        adding.Children.Add(add);

        var under = new StackPanel { Spacing = 8 };
        under.Children.Add(sites);
        under.Children.Add(adding);
        var row = Row(Ui.Globe, "Off on these sites", "For a site blocking broke. Its subdomains are included", under: under);

        follow.Add(() =>
        {
            sites.Children.Clear();
            foreach (var site in Shield.Paused) sites.Children.Add(Site(site));
            sites.Visibility = sites.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            row.Visibility = Shield.Enabled ? Visibility.Visible : Visibility.Collapsed;
        });
        return row;
    }

    /// Расширения: поставить из папки и то, что уже стоит, с переключателем и
    /// меню (настройки расширения, убрать). Имя и «включено» знает движок, версию
    /// и откуда — installed.json. ExtensionsPage из апстрима; Web Store — потом.
    StackPanel[] ExtensionRows(Tabs tabs)
    {
        var list = new StackPanel();
        var trouble = new TextBlock { FontSize = 11.5, Foreground = Palette.Danger, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        // Любое дело к движку: ошибку показываем под строкой, а не роняем окно.
        async Task Ask(Func<CoreWebView2Profile, Task> act)
        {
            try
            {
                await tabs.WithProfile(act);
                trouble.Visibility = Visibility.Collapsed;
            }
            catch (Exception e) when (e is COMException or InvalidDataException or IOException or UnauthorizedAccessException
                or ArgumentException or HttpRequestException or TaskCanceledException)
            {
                trouble.Text = $"Couldn't do that: {e.Message}";
                trouble.Visibility = Visibility.Visible;
            }
        }

        async void Refresh()
        {
            List<(Installed Record, string Name, bool Enabled)> mine = [];
            await Ask(async profile => mine = await Addons.Mine(profile));
            list.Children.Clear();
            foreach (var (record, name, enabled) in mine) list.Children.Add(One(record, name, enabled));
        }

        StackPanel One(Installed record, string name, bool enabled)
        {
            var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
            if (Addons.Options(record) is { } options)
            {
                var open = new MenuFlyoutItem { Text = "Options" };
                open.Click += (_, _) => tabs.Open(options);
                menu.Items.Add(open);
            }
            var remove = new MenuFlyoutItem { Text = "Remove" };
            remove.Click += async (_, _) => await Ask(profile => Addons.Remove(profile, record.Id));
            menu.Items.Add(remove);
            var more = Pill($"More for {name}", Ui.Glyph(Ui.More, 14, Palette.InkSoft));
            more.Flyout = menu;

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            controls.Children.Add(more);
            controls.Children.Add(Switch($"{name} on", () => enabled, on => _ = Ask(profile => Addons.Enable(profile, record.Id, on)), tracked: false));
            var from = record.Source switch
            {
                { } store when store.StartsWith("https://chromewebstore.google.com/", StringComparison.Ordinal) => "Chrome Web Store",
                { } source => $"From “{System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(source))}”",
                null => "From a folder",
            };
            return Row(Ui.Puzzle, name, $"Version {record.Version} · {from}", controls);
        }

        async void Choose()
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            if (await picker.PickSingleFolderAsync() is { } folder) await Ask(profile => Addons.Install(profile, folder.Path));
        }
        var load = Row(Ui.FolderOpen, "Load an unpacked extension",
            "A folder with a manifest.json: your own, or one exported from another browser. aerio keeps a copy of it",
            Pill("Load an unpacked extension", Word("Choose…"), Choose));

        // Из магазина: ссылка на страницу расширения или его id. Пока качается,
        // кнопка говорит «Adding…» и второй раз не нажимается.
        var (frame, box) = Entry("Chrome Web Store link or extension id", "Paste a link to an extension, or its id");
        var word = Word("Add");
        var add = Pill("Add from the Chrome Web Store", word);
        async void Add()
        {
            if (!add.IsEnabled) return;
            var text = box.Text;
            add.IsEnabled = false;
            word.Text = "Adding…";
            await Ask(profile => Addons.InstallFromStore(profile, text));
            add.IsEnabled = true;
            word.Text = "Add";
            if (trouble.Visibility == Visibility.Collapsed) box.Text = "";
        }
        add.Click += (_, _) => Add();
        box.KeyDown += (_, press) =>
        {
            if (press.Key != VirtualKey.Enter) return;
            press.Handled = true;
            Add();
        };
        var adding = new Grid { ColumnSpacing = 8 };
        adding.ColumnDefinitions.Add(new ColumnDefinition());
        adding.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(add, 1);
        adding.Children.Add(frame);
        adding.Children.Add(add);
        var under = new StackPanel { Spacing = 8 };
        under.Children.Add(adding);
        under.Children.Add(trouble);
        var store = Row(Ui.Store, "Add from the Chrome Web Store", "Find it in the store, then paste its page's address here",
            Pill("Open the Chrome Web Store", Word("Open the Store"), () => tabs.Open(new Uri("https://chromewebstore.google.com/"))), under);

        Loaded += (_, _) =>
        {
            Addons.Changed += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => Addons.Changed -= Refresh;
        return [store, load, list];
    }

    /// Строка списка сайтов: имя и крестик, который возвращает сайт под блокировщик.
    static Grid Site(string site)
    {
        var line = new Grid { Height = 24 };
        line.Children.Add(new TextBlock
        {
            Text = site, FontSize = 12.5, Foreground = Palette.Ink, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 28, 0),
        });
        var disc = new Border
        {
            Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Background = Palette.InkDisc,
            Child = Ui.Glyph(Ui.Cross, 12, Palette.Muted, 2.5),
        };
        // Нажимается квадрат побольше кружка.
        var remove = Ui.Bare(new Button { CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Right },
            $"Remove {site}", new Border { Width = 24, Height = 24, Background = Palette.Clear, Child = disc });
        remove.Click += (_, _) => Shield.Pause(site, false);
        line.Children.Add(remove);
        return line;
    }
}
