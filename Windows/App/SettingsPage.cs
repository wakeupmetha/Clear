using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using Windows.UI.Core;
using static Browser.Ui;

namespace Browser;

/// Настройки: своя страница во вкладке (browser://settings), а не окно поверх.
/// Вид перенесён из Sources/Search/Settings.swift: группы с маленьким
/// заголовком, в каждой карточка строк; строка — значок в плитке, что это,
/// одна строка о том, что она делает, и элемент управления справа.
///
/// Разделы слева, как в апстриме, справа — один раздел. Поиска по настройкам,
/// как в апстриме, нет: разделов восемь, строк в них немного.
///
/// Страница — только вид на Settings. Элементы пишут туда, а окно, вкладки и
/// сама страница узнают об изменении из Settings.Changed. Поэтому Ctrl+S при
/// открытых настройках двигает и переключатель на странице.
sealed partial class SettingsPage : Grid
{
    /// Каждый элемент умеет привести себя к тому, что записано в настройках.
    readonly List<Action> follow = [];
    /// Элементы сейчас приводятся к настройкам: это не решение человека,
    /// записывать его обратно не надо.
    bool following;

    /// Строка сочетаний ждёт новое сочетание: сочетания окна молчат (MainWindow).
    internal static bool Recording { get; private set; }

    public SettingsPage(Tabs tabs)
    {
        Background = Palette.Ground;

        // Узкой держим колонку, а не прокрутку: пустое место по бокам тоже
        // должно прокручивать страницу.
        var column = new StackPanel { MaxWidth = 600, Padding = new Thickness(24, 26, 24, 24), Spacing = 22 };
        var scroll = new ScrollViewer
        {
            Content = column, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        };
        // Ширина явно, как у истории: с одним MaxWidth колонка вставала не по
        // середине, и у разных разделов — по-разному.
        scroll.SizeChanged += (_, _) => column.Width = Math.Min(600, scroll.ActualWidth);
        var (permissions, data) = SiteRows(tabs);
        var forms = FormRows(tabs);
        // Разделы, как в апстриме (Settings.swift, Page; просьба владельца
        // 2026-11-21: «по категориям, как в оригинале»), и только те, где у нас
        // есть что ставить: Spaces & Pins появится со своими первыми строками.
        // Блоки рельса — тоже апстрима: браузер, твои данные и
        // инструменты, About.
        (string Key, string Title, string Glyph, StackPanel[] Groups)[][] blocks =
        [
            [
                ("general", "General", Ui.Gear, [Group("Downloads", Ui.Download, SaveTo(),
                Row(Ui.Download, "Ask where to save each file", "A save dialog for every download",
                    Switch("Ask where to save each file", () => Settings.Bool("downloads.ask"), on => Settings.Set("downloads.ask", on))))]),
                ("appearance", "Appearance", Ui.Colour, [Group("Light and dark", Ui.SunMoon,
                Row(Ui.SunMoon, "Appearance", "Light, dark, or as Windows has it. Pages follow too",
                    Choice("Appearance", [("light", "Light"), ("dark", "Dark"), ("system", "System")],
                        () => Settings.Text("look") is "light" or "dark" ? Settings.Text("look")! : "system",
                        look => Settings.Set("look", look)))),
                    Group("Window", Ui.ClosePane,
                Row(Ui.ClosePane, "Fold the tab column", "The page takes the whole window; in full screen the tabs come out at the left edge" + Keyed("view.fold"),
                    Switch("Fold the tab column", () => Settings.Bool("sidebar.folded"), on => Settings.Set("sidebar.folded", on))),
                Row(Ui.Focus, "Focus mode", "Only the page: the tabs come out at the left edge, the top bar at the top edge" + Keyed("view.focus"),
                    Switch("Focus mode", () => Settings.Bool("window.focus"), on => Settings.Set("window.focus", on))))]),
                // Сон вкладок: SettingsIndex.swift, tabs.sleep — Never или как скоро.
                ("tabs", "Tabs", Ui.TwoTabs, [Group("Memory", Ui.Moon,
                Row(Ui.Moon, "Sleep tabs you aren't using", "A tab left alone lets its page go and loads it again when you go back to it",
                    Choice("Sleep tabs you aren't using", [("never", "Never"), ("later", "Later"), ("normal", "Normal"), ("sooner", "Sooner")],
                        () => TabSleep.Choice(Settings.Bool("tabs.sleep", true), Settings.Text("tabs.sleep.pace")),
                        choice =>
                        {
                            if (choice != "never") Settings.Set("tabs.sleep.pace", choice);
                            Settings.Set("tabs.sleep", choice != "never");
                        })))]),
                ("browsing", "Browsing", Ui.Globe, [Group("Searching", Ui.Find,
                Row(Ui.TextSearch, "Search with", "Where words that aren't an address go", EnginePicker()),
                CustomEngine())]),
            ],
            [
                ("passwords", "Passwords", Ui.KeyRound, [Group("Your passwords", Ui.KeyRound, forms[0], forms[2]), Group("Filling in forms", Ui.TextCursorInput, forms[1])]),
                ("privacy", "Privacy", Ui.Guard, [Group("Blocking", Ui.Guard,
                Row(Ui.ShieldBan, "Block ads and trackers",
                    Shield.Delegated ? "uBlock Origin blocks them, YouTube ads too" : "Third parties whose only job is to watch or to sell",
                    Switch("Block ads and trackers", () => Shield.Enabled, on => Shield.Enabled = on)),
                // sites.keep — ключ апстрима, и он про обратное: «оставить сайтам входы».
                Row(Ui.EyeOff, "Prevent cross-site tracking", "The engine's own tracking prevention, balanced as in Edge",
                    Switch("Prevent cross-site tracking", () => !Settings.Bool("sites.keep"), on => Settings.Set("sites.keep", !on))),
                // С uBlock свой список сайтов не действует: доверенные сайты — в его панели.
                Shield.Delegated
                    ? Row(Ui.Sliders, "uBlock Origin", "Filter lists, trusted sites, the logger. uBlock Origin 1.76.6, GPLv3",
                        Pill("Open uBlock Origin settings", Word("Open"), () => { if (UBlock.Dashboard is { } dashboard) tabs.Open(dashboard); }))
                    : PausedSites()),
                    Group("Site Permissions", Ui.ShieldCheck, permissions), Group("Cookies and Site Data", Ui.Cookie, data)]),
                ("extensions", "Extensions", Ui.Puzzle, [Group("Installed", Ui.Puzzle, ExtensionRows(tabs))]),
                ("keyboard", "Keyboard", Ui.Keyboard, [Group("Shortcuts", Ui.Keyboard, ShortcutRows())]),
            ],
            [
                ("about", "About", Ui.Info, [Group("Updates", Ui.Reload, UpdateRow()), Group("Developer", Ui.Cpu,
                Row(Ui.Flask, "Engine experiments", "The engine's own flags page, edge://flags. For testing: a flag can break pages",
                    Pill("Open engine experiments", Word("Open"), () => tabs.Open(new Uri("edge://flags")))))]),
            ],
        ];
        var sections = blocks.SelectMany(block => block).ToArray();

        // Рельс разделов слева: Settings.swift, pages.
        var rail = new StackPanel { Width = Metrics.SettingsRail, Padding = new Thickness(8), Spacing = 2, Background = Palette.Hover };
        rail.Children.Add(new TextBlock
        {
            Text = "Settings", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Palette.Ink,
            Margin = new Thickness(10, 14, 10, 10),
        });
        var marks = new List<(string Key, RadioButton Option)>();
        var names = new List<(RadioButton Option, TextBlock Text, string Title)>();
        foreach (var block in blocks)
        {
            if (marks.Count > 0) rail.Children.Add(new Border { Height = 1, Background = Palette.Hairline, Margin = new Thickness(10, 7, 10, 7) });
            foreach (var (key, title, glyph, _) in block) marks.Add((key, Section(key, title, glyph)));
        }

        // Раздел — строка рельса: значок и имя, выбранный — белым, как в апстриме.
        RadioButton Section(string key, string title, string glyph)
        {
            var icon = Ui.Glyph(glyph, 15, Palette.InkSoft);
            icon.VerticalAlignment = VerticalAlignment.Center;
            // Посередине — буквы, а не строка шрифта: как подписи внизу колонки (MainWindow.Quiet).
            var text = new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight };
            var plate = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10, Height = 30, Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(7),
            };
            plate.Children.Add(icon);
            plate.Children.Add(text);
            var option = Ui.Bare(new RadioButton
            {
                GroupName = "Settings section", CornerRadius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Stretch,
            }, title, plate);
            var over = false;
            void Paint()
            {
                var on = option.IsChecked == true;
                plate.Background = on ? Palette.Ground : over ? Palette.Wash : Palette.Clear;
                text.Foreground = on ? Palette.Ink : Palette.InkSoft;
                text.FontWeight = on ? FontWeights.Medium : FontWeights.Normal;
            }
            option.PointerEntered += (_, _) => { over = true; Paint(); };
            option.PointerExited += (_, _) => { over = false; Paint(); };
            option.Checked += (_, _) =>
            {
                Paint();
                if (!following) Settings.Set("settings.page", key);
            };
            option.Unchecked += (_, _) => Paint();
            Paint();
            rail.Children.Add(option);
            names.Add((option, text, title));
            return option;
        }

        // Узкая страница (окно у своего предела, колонка вкладок открыта): рельс —
        // одни значки с подсказками, поля колонки уже. Иначе строкам настроек
        // оставалось по букве в ряд.
        var heading = (TextBlock)rail.Children[0];
        var narrow = false;
        SizeChanged += (_, _) =>
        {
            var now = ActualWidth < Metrics.SettingsRail + Metrics.SettingsNarrowest;
            if (now == narrow) return;
            narrow = now;
            rail.Width = narrow ? Metrics.SettingsRailNarrow : Metrics.SettingsRail;
            heading.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            foreach (var (option, text, name) in names)
            {
                text.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
                ToolTipService.SetToolTip(option, narrow ? name : null);
            }
            column.Padding = new Thickness(narrow ? 12 : 24, 26, narrow ? 12 : 24, 24);
        };

        // Показан раздел из settings.page (ключ апстрима): его ставит рельс, а
        // проверки — тест-каналом (set). Незнакомый — первый. Группы собраны
        // один раз, в колонке — только показанный раздел.
        string? shown = null;
        follow.Add(() =>
        {
            var key = Settings.Text("settings.page");
            var section = sections.FirstOrDefault(section => section.Key == key);
            if (section.Key is null) section = sections[0];
            if (section.Key == shown) return;
            shown = section.Key;
            foreach (var (mark, option) in marks) option.IsChecked = mark == shown;
            column.Children.Clear();
            column.Children.Add(Heading(section.Glyph, section.Title));
            foreach (var group in section.Groups) column.Children.Add(group);
            scroll.ChangeView(null, 0, null, true);
        });

        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        ColumnDefinitions.Add(new ColumnDefinition());
        Children.Add(rail);
        var hairline = new Border { Background = Palette.Hairline };
        SetColumn(hairline, 1);
        Children.Add(hairline);
        SetColumn(scroll, 2);
        Children.Add(scroll);

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
    internal static StackPanel Group(string title, string glyph, params StackPanel[] rows)
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
    internal static StackPanel Row(string glyph, string title, string detail, UIElement? control = null, UIElement? under = null)
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

    /// Куда ложатся загрузки (downloads.folder апстрима): путь и «Change…».
    /// Движок берёт папку из настройки (Tabs.Dress); без неё — Загрузки Windows.
    StackPanel SaveTo()
    {
        static string Folder() => Settings.Text("downloads") ?? "The Downloads folder";
        var row = Row(Ui.FolderOpen, "Save to", Folder(), Pill("Change where downloads are saved", Word("Change…"), Change));
        // Подпись строки (Row): второй текст её слов. Путь сменили — подпись за ним.
        var said = (TextBlock)((StackPanel)((Grid)row.Children[1]).Children[1]).Children[1];
        follow.Add(() => said.Text = Folder());
        async void Change()
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            if (await picker.PickSingleFolderAsync() is { } folder) Settings.Set("downloads", folder.Path);
        }
        return row;
    }

    /// Обновить браузер изнутри: пересобрать из исходников на этом компьютере и
    /// поставить (Updater). Ход и ошибка — строкой под описанием.
    static StackPanel UpdateRow()
    {
        var status = new TextBlock { FontSize = 11.5, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var word = Word("Update");
        var pill = Pill($"Rebuild and update {Store.Name}", word);
        // Кнопку не выключаем: выключенная теряет фокус, и он прыгает в колонку.
        // Второе нажатие во время сборки Updater сам пропускает.
        void Building()
        {
            word.Text = "Building…";
            status.Visibility = Visibility.Visible;
            status.Foreground = Palette.Muted;
            status.Text = $"Building from the source, then installing. Takes a minute or two; {Store.Name} restarts";
        }
        if (Updater.Busy) Building();
        pill.Click += async (_, _) =>
        {
            if (Updater.Busy) return;
            Building();
            try { await Updater.Update(App.Quit); }
            // Сборка и установщик падают чем угодно; окно не должно падать вместе с ними.
            catch (Exception e)
            {
                status.Text = $"Couldn't update: {e.Message}";
                status.Foreground = Palette.Danger;
                word.Text = "Update";
            }
        };
        var detail = Updater.Source is null ? $"Version {Updater.Version}. The source isn't on this computer"
            : !Updater.Installed ? $"Version {Updater.Version}. Only the installed {Store.Name} updates itself"
            : $"Version {Updater.Version}. Builds {Store.Name} again from the source on this computer and installs it";
        return Row(Ui.Reload, $"Update {Store.Name}", detail, Updater.Installed ? pill : null, status);
    }

    /// Сочетание команды в конце описания строки: «. Ctrl+S». Берётся, когда
    /// страница строится; ponytail: сменённое здесь же сочетание в описании
    /// видно, когда настройки откроют заново.
    static string Keyed(string id) => Shortcuts.Key(id) is { } key ? $". {key.Display}" : "";

    /// Сочетания команд окна: Settings › Keyboard & Mouse › Shortcuts апстрима
    /// (ShortcutsPage.swift). Поиска нет: команд пятнадцать, видны все сразу.
    StackPanel[] ShortcutRows()
    {
        var rows = Shortcuts.All.Select(command => Line(command.Title, command.Also is { } also ? $"{also} too" : null, KeyBox(command))).ToList();
        var all = Line("All shortcuts", $"Back to the keys {Store.Name} came with",
            Pill("Reset all shortcuts to defaults", Word("Reset All"), Shortcuts.ResetAll));
        follow.Add(() => all.Visibility = Shortcuts.AnyChanged ? Visibility.Visible : Visibility.Collapsed);
        rows.Add(all);
        return [.. rows];
    }

    /// Сочетание команды: KeyBox апстрима. Нажать и набрать новое — оно
    /// переходит сюда от команды, у которой было (если было — второе нажатие
    /// того же сочетания как согласие). Esc — оставить как было, Backspace или
    /// Delete — снять. Меню (правая кнопка, клавиша меню): снять или вернуть
    /// умолчание.
    Button KeyBox(Command command)
    {
        // Посередине по буквам, с полем снизу на две точки больше: как у Ui.Pill.
        var word = new TextBlock
        {
            FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextLineBounds = TextLineBounds.Tight,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var plate = new Border
        {
            MinWidth = 64, MinHeight = 24, MaxWidth = 280, Padding = new Thickness(10, 0, 10, 2),
            CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Child = word,
        };
        var box = Ui.Bare(new Button { CornerRadius = new CornerRadius(7) }, $"{command.Title} shortcut", plate);
        ToolTipService.SetToolTip(box, "Click, then press the new shortcut");
        var listening = false;
        var hovering = false;
        string? note = null;
        // Сочетание другой команды, нажатое один раз: второе нажатие забирает его.
        Combo? taking = null;

        void Paint()
        {
            var key = Shortcuts.Key(command.Id);
            word.Text = taking is { } wanted && Shortcuts.Owner(wanted, command.Id) is { } other ? $"Used by {other.Title} — press again"
                : note ?? (listening ? "Type a shortcut" : key?.Display ?? "None");
            word.FontWeight = Shortcuts.IsChanged(command.Id) ? FontWeights.SemiBold : FontWeights.Normal;
            word.Foreground = listening || key is not null ? Palette.Ink : Palette.Muted;
            plate.Background = listening || hovering ? Palette.Hover : Palette.Ground;
            plate.BorderBrush = listening ? Palette.Muted : Palette.Hairline;
            // Диктор читает имя, а за ним — что сейчас написано на кнопке.
            AutomationProperties.SetHelpText(box, word.Text);
        }
        void Listen()
        {
            listening = true;
            note = null;
            taking = null;
            Recording = true;
            Paint();
        }
        void Stop()
        {
            listening = false;
            note = null;
            taking = null;
            Recording = false;
            Paint();
        }
        // Почему это сочетание не годится — на миг, слушать не перестаём.
        async void Say(string why)
        {
            note = why;
            Paint();
            await Task.Delay(1200);
            if (note != why) return;
            note = null;
            Paint();
        }

        box.Click += (_, _) => { if (listening) Stop(); else Listen(); };
        box.LostFocus += (_, _) => { if (listening) Stop(); };
        // Закрытая страница не должна оставить окно без сочетаний.
        box.Unloaded += (_, _) => { if (listening) Stop(); };
        // Preview: раньше кнопки (Enter её нажал бы) и сочетаний окна.
        box.PreviewKeyDown += (_, press) =>
        {
            if (!listening) return;
            press.Handled = true;
            bool ctrl = Down(VirtualKey.Control), shift = Down(VirtualKey.Shift), alt = Down(VirtualKey.Menu);
            if (!ctrl && !shift && !alt && press.Key == VirtualKey.Escape)
            {
                Stop();
                return;
            }
            if (!ctrl && !shift && !alt && press.Key is VirtualKey.Back or VirtualKey.Delete)
            {
                Shortcuts.Clear(command.Id);
                Stop();
                return;
            }
            // Сам Ctrl, Shift или Alt: сочетание ещё набирают.
            if (Combo.FromCode((int)press.Key, ctrl, shift, alt) is not { } combo) return;
            if (Shortcuts.Refusal(combo) is { } why)
            {
                Say(why);
                return;
            }
            if (Shortcuts.Owner(combo, command.Id) is not null && taking != combo)
            {
                taking = combo;
                Paint();
                return;
            }
            Shortcuts.Assign(combo, command.Id);
            Stop();
        };
        // Пробел нажимает кнопку, когда его отпускают: пока слушаем, это не нажатие.
        box.PreviewKeyUp += (_, press) => { if (listening) press.Handled = true; };
        box.PointerEntered += (_, _) =>
        {
            hovering = true;
            Paint();
        };
        box.PointerExited += (_, _) =>
        {
            hovering = false;
            Paint();
        };

        var menu = new MenuFlyout();
        var clear = new MenuFlyoutItem { Text = "Clear Shortcut" };
        clear.Click += (_, _) => Shortcuts.Clear(command.Id);
        var reset = new MenuFlyoutItem { Text = "Reset to Default" };
        reset.Click += (_, _) => Shortcuts.Reset(command.Id);
        menu.Items.Add(clear);
        menu.Items.Add(reset);
        menu.Opening += (_, _) =>
        {
            clear.IsEnabled = Shortcuts.Key(command.Id) is not null;
            reset.IsEnabled = Shortcuts.IsChanged(command.Id);
        };
        box.ContextFlyout = menu;

        follow.Add(Paint);
        return box;
    }

    static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// Строка без значка: название, под ним мелко пояснение, справа элемент.
    /// Line апстрима. Первой идёт черта, как у Row.
    static StackPanel Line(string title, string? detail, FrameworkElement control)
    {
        var grid = new Grid { Padding = new Thickness(14, 8, 14, 8), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Palette.Ink, TextWrapping = TextWrapping.Wrap });
        if (detail is not null) words.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = Palette.Muted });
        grid.Children.Add(words);
        control.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(control, 1);
        grid.Children.Add(control);

        var row = new StackPanel();
        row.Children.Add(new Border { Height = 1, Background = Palette.Hairline });
        row.Children.Add(grid);
        return row;
    }

    /// Переключатель (Ui.Switch), который держится за настройку.
    /// tracked: false — состояние не из Settings (расширение): читается один раз.
    ToggleButton Switch(string name, Func<bool> read, Action<bool> write, bool tracked = true)
    {
        var toggle = Ui.Switch(name, on => { if (!following) write(on); });
        void Read() => toggle.IsChecked = read();
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

    /// Выбор из вариантов (Ui.Segmented), который держится за настройку.
    Border Choice(string name, (string Value, string Title)[] options, Func<string> read, Action<string> write)
    {
        var (view, show) = Ui.Segmented(name, options, value => { if (!following) write(value); });
        follow.Add(() => show(read()));
        return view;
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
            foreach (var site in Shield.Paused) sites.Children.Add(Site(site, null, $"Remove {site}", () => Shield.Pause(site, false)));
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
        var trouble = Trouble();
        Task Ask(Func<CoreWebView2Profile, Task> act) => Attempt(tabs, trouble, core => act(core.Profile));

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
            $"A folder with a manifest.json: your own, or one exported from another browser. {Store.Name} keeps a copy of it",
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

    /// Пароли и формы запоминает сам движок. Ключи апстрима: passwords.save и
    /// contacts.fill. ponytail: списка сохранённого у движка нет — ни API, ни
    /// своей страницы, — поэтому только «забыть всё»; свой список, когда
    /// движок его отдаст.
    StackPanel[] FormRows(Tabs tabs)
    {
        var trouble = Trouble();
        var word = Word("Forget All");
        var sure = false;
        async void Forget()
        {
            // Пароли не вернуть: первое нажатие только переспрашивает.
            if (!sure)
            {
                sure = true;
                word.Text = "Click again to forget";
                await Task.Delay(4000);
                sure = false;
                word.Text = "Forget All";
                return;
            }
            sure = false;
            word.Text = "Forget All";
            await Attempt(tabs, trouble, core => core.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.PasswordAutosave | CoreWebView2BrowsingDataKinds.GeneralAutofill).AsTask());
        }
        return [
            Row(Ui.KeyRound, "Offer to save passwords", "Kept by the engine for this Windows account, filled in next time",
                Switch("Offer to save passwords", () => Settings.Bool("passwords.save", true), on => Settings.Set("passwords.save", on))),
            Row(Ui.TextCursorInput, "Fill in forms", "Names, addresses, email and phone you typed before, offered under their box",
                Switch("Fill in forms", () => Settings.Bool("contacts.fill", true), on => Settings.Set("contacts.fill", on))),
            Row(Ui.Trash, "Saved passwords and form entries", "Not listed here: forgetting is for every site at once",
                Pill("Forget saved passwords and form entries", word, Forget), trouble),
        ];
    }

    /// Разрешения сайтов и их куки с данными — списками по сайтам (просьба
    /// владельца 2026-11-20: свои разделы вместо голых страниц движка). В
    /// апстриме: privacy.forget (разрешения) и clear (куки — только все сразу).
    (StackPanel Permissions, StackPanel Data) SiteRows(Tabs tabs)
    {
        // Списки из движка — сразу простыми значениями: невидимый движок, у
        // которого их спросили, к показу уже закрыт.
        List<(CoreWebView2PermissionKind Kind, string Origin, CoreWebView2PermissionState State)> saved = [];
        List<(string Site, int Count)> sites = [];

        var allowed = new StackPanel { Spacing = 4 };
        var permissionTrouble = Trouble();
        var forget = Pill("Forget all site permissions", Word("Forget All"));
        var under = new StackPanel { Spacing = 8 };
        under.Children.Add(allowed);
        under.Children.Add(permissionTrouble);
        var permissions = Row(Ui.ShieldCheck, "Camera, microphone, location and notifications", "What each site was allowed or refused", forget, under);

        var (frame, box) = Entry("Find a site", "Find a site", Ui.Find);
        var kept = new StackPanel { Spacing = 4 };
        var dataTrouble = Trouble();
        var below = new StackPanel { Spacing = 8 };
        below.Children.Add(frame);
        below.Children.Add(kept);
        below.Children.Add(dataTrouble);
        var data = Row(Ui.Cookie, "Cookies and site data", "Clearing a site signs you out of it and wipes what it stored",
            Pill("Sign out of everything", Word("Clear All"), ClearAll), below);

        async void Read()
        {
            await Attempt(tabs, dataTrouble, async core =>
            {
                saved = [.. (await core.Profile.GetNonDefaultPermissionSettingsAsync())
                    .Select(setting => (setting.PermissionKind, setting.PermissionOrigin, setting.PermissionState))];
                sites = [.. (await core.CookieManager.GetCookiesAsync("")).GroupBy(cookie => Address.Site(cookie.Domain))
                    .Select(site => (site.Key, site.Count())).OrderBy(site => site.Key, StringComparer.Ordinal)];
            });
            ShowPermissions();
            ShowData();
        }

        void ShowPermissions()
        {
            allowed.Children.Clear();
            foreach (var origin in saved.GroupBy(setting => setting.Origin).OrderBy(origin => Shown(origin.Key), StringComparer.Ordinal))
            {
                var site = Shown(origin.Key);
                var choices = string.Join(" · ", origin.Select(setting => $"{Kind(setting.Kind)}: {(setting.State == CoreWebView2PermissionState.Allow ? "Allow" : "Refuse")}"));
                allowed.Children.Add(Site(site, choices, $"Forget choices for {site}", () => Forget([.. origin])));
            }
            if (saved.Count == 0) allowed.Children.Add(Quiet("No site has been allowed or refused anything yet"));
            forget.Visibility = saved.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        async void Forget((CoreWebView2PermissionKind Kind, string Origin, CoreWebView2PermissionState State)[] settings)
        {
            await Attempt(tabs, permissionTrouble, async core =>
            {
                foreach (var setting in settings)
                    await core.Profile.SetPermissionStateAsync(setting.Kind, setting.Origin, CoreWebView2PermissionState.Default);
            });
            Read();
        }
        forget.Click += (_, _) => Forget([.. saved]);

        void ShowData()
        {
            kept.Children.Clear();
            var words = box.Text.Trim();
            var found = sites.Where(site => site.Site.Contains(words, StringComparison.OrdinalIgnoreCase)).ToList();
            // ponytail: первые 30, дальше — уточнить поиском; мешает — список
            // с прокруткой (ItemsRepeater, как в истории).
            foreach (var (site, count) in found.Take(30))
                kept.Children.Add(Site(site, count == 1 ? "1 cookie" : $"{count} cookies", $"Clear data of {site}", () => Wipe(site)));
            if (found.Count > 30) kept.Children.Add(Quiet($"And {found.Count - 30} more. Type to narrow"));
            if (sites.Count == 0) kept.Children.Add(Quiet("No site keeps cookies"));
            else if (found.Count == 0) kept.Children.Add(Quiet("No site called that"));
            frame.Visibility = sites.Count > 8 ? Visibility.Visible : Visibility.Collapsed;
        }
        box.TextChanged += (_, _) => ShowData();

        // Куки сайта и хранилища его источников: localStorage, IndexedDB, кэш,
        // service worker. У WebView2 стирание только всего профиля — берём
        // отладочный протокол, как карточка сайта.
        async void Wipe(string site)
        {
            await Attempt(tabs, dataTrouble, async core =>
            {
                var jar = core.CookieManager;
                var mine = (await jar.GetCookiesAsync("")).Where(cookie => Address.Site(cookie.Domain) == site).ToList();
                var hosts = mine.Select(cookie => cookie.Domain).ToList();
                foreach (var cookie in mine) jar.DeleteCookie(cookie);
                foreach (var origin in Address.Origins(site, hosts, tabs.History.Everything().Select(trace => trace.Url)))
                    await core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin", new JsonObject { ["origin"] = origin, ["storageTypes"] = "all" }.ToJsonString());
            });
            Read();
        }

        async void ClearAll()
        {
            await Attempt(tabs, dataTrouble, core => core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllSite).AsTask());
            Read();
        }

        Loaded += (_, _) => Read();
        return (permissions, data);
    }

    /// Разрешение, как его называет карточка сайта (SiteCard), и остальные,
    /// которые движок тоже помнит.
    static string Kind(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Geolocation => "Location",
        CoreWebView2PermissionKind.OtherSensors => "Motion sensors",
        CoreWebView2PermissionKind.ClipboardRead => "Clipboard",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "Several downloads",
        CoreWebView2PermissionKind.FileReadWrite => "Files",
        CoreWebView2PermissionKind.LocalFonts => "Fonts",
        CoreWebView2PermissionKind.MidiSystemExclusiveMessages => "MIDI",
        CoreWebView2PermissionKind.WindowManagement => "Window placement",
        _ => kind.ToString(),
    };

    /// Источник, как его читает человек: хост без www, порт — если не обычный.
    static string Shown(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var url) ? Address.WithoutWWW(url.Host) + (url.IsDefaultPort ? "" : $":{url.Port}") : origin;

    static TextBlock Quiet(string text) => new() { Text = text, FontSize = 12, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap };

    static TextBlock Trouble() => new() { FontSize = 11.5, Foreground = Palette.Danger, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    /// Любое дело к движку: ошибку показываем строкой trouble, а не роняем окно.
    static async Task Attempt(Tabs tabs, TextBlock trouble, Func<CoreWebView2, Task> act)
    {
        try
        {
            await tabs.WithEngine(act);
            trouble.Visibility = Visibility.Collapsed;
        }
        catch (Exception e) when (e is COMException or InvalidDataException or IOException or UnauthorizedAccessException
            or ArgumentException or HttpRequestException or TaskCanceledException)
        {
            trouble.Text = $"Couldn't do that: {e.Message}";
            trouble.Visibility = Visibility.Visible;
        }
    }

    /// Строка списка сайтов: имя, под ним мелко — что о нём помнят, крестик справа.
    static Grid Site(string site, string? detail, string name, Action remove)
    {
        var line = new Grid { MinHeight = 24 };
        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 28, 0) };
        words.Children.Add(new TextBlock { Text = site, FontSize = 12.5, Foreground = Palette.Ink, TextTrimming = TextTrimming.CharacterEllipsis });
        if (detail is not null)
            words.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = Palette.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        line.Children.Add(words);
        var disc = new Border
        {
            Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Background = Palette.InkDisc,
            Child = Ui.Glyph(Ui.Cross, 12, Palette.Muted, 2.5),
        };
        // Нажимается квадрат побольше кружка.
        var cross = Ui.Bare(new Button { CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Right },
            name, new Border { Width = 24, Height = 24, Background = Palette.Clear, Child = disc });
        cross.Click += (_, _) => remove();
        line.Children.Add(cross);
        return line;
    }
}
