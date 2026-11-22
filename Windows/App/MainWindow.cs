using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace Browser;

/// Окно: колонка вкладок слева, страница карточкой справа, сверху полоса, за
/// которую окно таскают. В полосе — тихая адресная строка (Bar); адрес
/// набирают в поле, которое всплывает по нажатию на неё, Ctrl+L, Ctrl+T или
/// по нажатию на живую вкладку.
///
/// Раскладка и размеры — из Sources/Search (Side.swift, Design.swift). Отличие
/// от macOS одно: кнопки окна системные и стоят справа, а не «светофором» слева.
sealed partial class MainWindow : Window
{
    readonly Grid root = new() { Background = Palette.Frame };
    readonly Grid band = new() { Background = Palette.Clear };
    readonly StackPanel doors = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    readonly StackPanel rows = new() { Spacing = Metrics.TabGap };
    readonly Grid pages = new();
    readonly Button newTab;
    readonly Grid card;
    readonly FindBar find;
    /// Что делает каждая команда окна (Bindings); тест-канал зовёт их по имени.
    Dictionary<string, Action> acts = [];
    readonly Button fold, back, forward, reload, bookmark, fetches;
    readonly Tabs tabs;
    readonly Field field;
    readonly Bar bar;
    readonly SiteCard site;
    readonly Actions actions;
    readonly StoreOffer offer;
    readonly Dictionary<Guid, TabRow> shown = [];
    /// Черта под закреплёнными вкладками.
    readonly Border pinLine = new() { Height = 1, Background = Palette.Hairline, Margin = new Thickness(10, 4, 10, 4) };
    /// Пока вкладку несут, — бледная плитка там, где она встанет (Carry): без неё
    /// несомая строка висит над соседней, и места, куда она упадёт, не видно.
    readonly Border slot = new()
    {
        Height = Metrics.Row, CornerRadius = new CornerRadius(Metrics.RowRadius), Background = Palette.InkTile,
        VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };
    /// Места полосы, отданные кнопкам и строке: ставятся заново, только когда сдвинулись.
    RectInt32[] holes = [];
    readonly CancellationTokenSource closing = new();

    public MainWindow()
    {
        Title = Store.Name;
        // Без этого у окна в панели задач и по Alt+Tab значок по умолчанию.
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "clear.ico"));
        ExtendsContentIntoTitleBar = true;
        Place();

        tabs = new Tabs(pages);
        field = new Field(tabs);

        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Metrics.Band) });
        root.RowDefinitions.Add(new RowDefinition());
        root.ColumnDefinitions.Add(side);
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // Полоса на всю ширину: за неё окно таскают, двойной щелчок разворачивает.
        Grid.SetColumnSpan(band, 2);
        root.Children.Add(band);

        fold = Door(Ui.ClosePane, "Hide sidebar", ToggleFold);
        back = Door(Ui.Back, "Back", tabs.Back);
        forward = Door(Ui.Forward, "Forward", tabs.Forward);
        reload = Door(Ui.Reload, "Reload", tabs.Reload);
        doors.Margin = new Thickness(Metrics.SidePadding, 0, 0, 0);
        doors.VerticalAlignment = VerticalAlignment.Center;
        doors.HorizontalAlignment = HorizontalAlignment.Left;
        doors.Children.Add(fold);
        doors.Children.Add(back);
        doors.Children.Add(forward);
        doors.Children.Add(reload);
        // Закладка: залита на сохранённой странице; щелчок сохраняет и убирает.
        bookmark = Door(Ui.Bookmark, "Bookmark this page", ToggleBookmark);
        doors.Children.Add(bookmark);
        Bookmarks.Changed += Bookmarked;
        // Загрузки: кнопка в полосе, пока файлы идут (FetchDoor апстрима).
        // ponytail: без круга, который наполняется, и без «всегда показывать».
        fetches = Door(Ui.Download, "Downloads", () => tabs.Show(Own.Downloads));
        fetches.Visibility = Visibility.Collapsed;
        doors.Children.Add(fetches);
        Fetching.Changed += () => fetches.Visibility = Fetching.Active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Кнопки остаются в полосе и когда столбец колонки сжат до нуля.
        Grid.SetColumnSpan(doors, 2);
        root.Children.Add(doors);
        // Над страницей, посередине карточки; поля строки ставит Passthrough.
        // Карточка сайта открывает окно uBlock из кнопок расширений.
        actions = new Actions(tabs);
        site = new SiteCard(tabs, actions.Press);
        // Нажатие придёт, когда строка уже собрана.
        bar = new Bar(EditAddress, () => site.Show(bar!.Site));
        Grid.SetColumn(bar.View, 1);
        root.Children.Add(bar.View);
        // Кнопки расширений — у правого края, перед кнопками окна (поле ставит Passthrough).
        actions.View.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(actions.View, 1);
        root.Children.Add(actions.View);

        newTab = Quiet(Ui.Plus, "New tab", () => tabs.Open(null));
        var scroll = new ScrollViewer
        {
            // Плитка — под строками, в той же клетке: строки рисуются поверх неё.
            Content = new Grid { Children = { slot, rows } }, Padding = new Thickness(Metrics.SidePadding, 0, Metrics.SidePadding, Metrics.SidePadding),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        };
        // История и настройки — внизу колонки, под вкладками: у окна нет меню,
        // откуда их звать. Ещё Ctrl+H, Ctrl+запятая и адреса browser://…
        var own = new StackPanel
        {
            Spacing = Metrics.TabGap, Margin = new Thickness(Metrics.SidePadding, 0, Metrics.SidePadding, Metrics.SidePadding),
        };
        own.Children.Add(Quiet(Ui.Clock, "History", () => tabs.Show(Own.History)));
        own.Children.Add(Quiet(Ui.Bookmark, "Bookmarks", () => tabs.Show(Own.Bookmarks)));
        own.Children.Add(Quiet(Ui.Gear, "Settings", () => tabs.Show(Own.Settings)));
        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition());
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(own, 1);
        column.Children.Add(scroll);
        column.Children.Add(own);

        // Карточка страницы: скруглённые углы и тонкий край. Поле адреса лежит
        // внутри неё, поверх страницы.
        card = new Grid
        {
            CornerRadius = new CornerRadius(Metrics.CardRadius), Background = Palette.Ground,
            BorderBrush = Palette.CardEdge, BorderThickness = new Thickness(1),
        };
        card.Children.Add(pages);
        find = new FindBar(tabs);
        card.Children.Add(find.View);
        card.Children.Add(field);
        offer = new StoreOffer(tabs);
        card.Children.Add(offer.View);
        Grid.SetRow(card, 1);
        Grid.SetColumn(card, 1);
        root.Children.Add(card);

        // Колонка добавляется после карточки: сложенная, она выезжает поверх неё.
        Folding(column);
        Focusing();

        Content = root;
        SetTitleBar(band);
        Bindings();
        Look();
        // Настройку меняют на странице настроек, сочетанием клавиш или через
        // тест-канал; окно узнаёт о ней здесь, откуда бы она ни пришла.
        Settings.Changed += key =>
        {
            if (key == "look") Look();
            else if (key is "shield" or "shield.paused")
            {
                tabs.Reshield();
                _ = UBlock.Follow();
            }
            else if (key == "sidebar.folded" && Settings.Bool(key) != folded) ToggleFold();
            else if (key == "window.focus") FollowFocus();
            else if (key is "sites.keep" or "passwords.save" or "contacts.fill" or "downloads") tabs.Redress();
            else if (key is "tabs.sleep" or "tabs.sleep.pace" or "sleep.after") tabs.Doze();
        };

        root.Loaded += (_, _) =>
        {
            Theme();
            Least();
            root.XamlRoot.Changed += (_, _) => Least();
            Passthrough();
            // Фокус, поставленный при восстановлении вкладок, ещё без окна, не
            // брался: его забирала первая кнопка полосы, с рамкой, а проснувшийся
            // движок потом не отнимал его у «колонки» (задача 31). Ставим заново.
            if (field.IsOpen) field.Take();
            else if (tabs.Live is { } live) tabs.Face(live);
        };
        root.ActualThemeChanged += (_, _) => Theme();
        // Колонку складывают с движением, окно тянут за край: места кнопок и
        // строки сверяются после каждой раскладки, а ставятся, только если сдвинулись.
        root.LayoutUpdated += (_, _) => Passthrough();

        tabs.Changed += Sync;
        tabs.Emptied += Close;
        field.Dismissed += () =>
        {
            // Передумали в пустой вкладке — возвращаемся, где были.
            if (tabs.Live is { IsBlank: true } blank && tabs.All.Count > 1) tabs.Close(blank);
            else tabs.Live?.View?.Focus(FocusState.Programmatic);
        };
        Closed += (_, _) =>
        {
            closing.Cancel();
            tabs.Save(now: true);
            tabs.Forget();
            tabs.History.Flush();
            Settings.Set("window.frame", WindowFrame.Write(new(normal.X, normal.Y, normal.Width, normal.Height)));
            // Закрыли с видео во весь экран — запоминаем, каким окно было до того.
            Settings.Set("window.zoomed", (immersed ? kept : AppWindow.Presenter) is OverlappedPresenter { State: OverlappedPresenterState.Maximized });
            Disk.Drain();
        };

        // uBlock в установке есть — свой список не нужен с самого начала (и
        // настройки показывают его панель); не встанет — UBlock.Ensure вернёт.
        Shield.Delegated = Directory.Exists(UBlock.Folder);
        tabs.Restore();
        Links.Serve(DispatcherQueue, Arrive);
        // Тест-канал слушает только в тестовом мире: установленный браузер
        // никому не даёт собой управлять.
        if (Store.Testing) _ = Bench.Serve(Bench.Pipe, Answer, closing.Token);
    }

    /// Адреса извне (Links): каждый — вкладкой, последний перед глазами. Пустая
    /// вкладка перед глазами берёт адрес сама, а не остаётся пустой рядом.
    void Arrive(IReadOnlyList<Uri> urls)
    {
        foreach (var url in urls)
            if (tabs.Live is { IsBlank: true, Private: false }) tabs.Go(url);
            else tabs.Open(url);
        // Свёрнутое окно разворачивается: ссылку открыли, чтобы её увидеть.
        if (!App.Quiet && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } shown) shown.Restore();
    }

    /// Рамка обычного, не развёрнутого окна: её и запоминаем при закрытии, чтобы
    /// окно, закрытое развёрнутым, свернулось потом в свой прежний размер.
    RectInt32 normal;

    /// Окно встаёт, где его закрыли (window.frame), если тот экран ещё есть;
    /// иначе — 1280 на 840, куда поставит система. Развернуть закрытое
    /// развёрнутым (window.zoomed) — дело App.OnLaunched: развёртывание
    /// показывает окно.
    void Place()
    {
        var screens = new List<WindowFrame.Box>();
        // По номеру, а не foreach: перечислитель этого списка в проекции C#
        // ненадёжен.
        var all = DisplayArea.FindAll();
        for (var i = 0; i < all.Count; i++)
        {
            var area = all[i].WorkArea;
            screens.Add(new(area.X, area.Y, area.Width, area.Height));
        }
        if (WindowFrame.Read(Settings.Text("window.frame")) is { } saved && WindowFrame.Fit(saved, screens) is { } box)
            AppWindow.MoveAndResize(new RectInt32(box.X, box.Y, box.Width, box.Height));
        else AppWindow.Resize(new SizeInt32(1280, 840));

        RectInt32 Now() => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        normal = Now();
        AppWindow.Changed += (_, change) =>
        {
            if ((change.DidPositionChange || change.DidSizeChange) && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
                normal = Now();
        };
    }

    /// Колонка, кнопки и заголовок окна приводятся к нынешним вкладкам.
    void Sync()
    {
        Immerse(tabs.Live?.View?.CoreWebView2?.ContainsFullScreenElement == true);
        foreach (var gone in shown.Keys.Except(tabs.All.Select(tab => tab.Id)).ToList())
        {
            shown[gone].Detach();
            shown.Remove(gone);
        }
        // Строка, оставшаяся на своём месте, из колонки не вынимается: вынутая
        // теряет фокус с клавиатуры, а вкладки меняются то и дело — заголовок,
        // загрузка, вкладка, открытая за спиной.
        // ponytail: на каждое изменение просматриваются все строки. При тысячах
        // вкладок трогать только изменившуюся.
        var place = 0;
        // Закреплённые — в начале списка (Tabs.Pin), под ними черта.
        var pins = tabs.All.Count(tab => tab.Pin is not null);
        for (var i = 0; i <= tabs.All.Count; i++)
        {
            if (i == pins && pins > 0) Seat(pinLine, place++);
            if (i == tabs.All.Count) break;
            var tab = tabs.All[i];
            if (!shown.TryGetValue(tab.Id, out var row))
                shown[tab.Id] = row = new TabRow(tab, tabs, () => Pick(tab), () => Drop(tab), () => { if (tab.Pin is null) tabs.Pin(tab); else tabs.Unpin(tab); }, () => tabs.Mute(tab),
                    () => tabs.Duplicate(tab), () => CopyAddress(tab), (travel, done) => Carry(tab, travel, done));
            row.Live = tab == tabs.Live;
            Seat(row, place++);
        }
        Seat(newTab, place++);
        while (rows.Children.Count > place) rows.Children.RemoveAt(place);

        Lit(back, tabs.CanGoBack);
        Lit(forward, tabs.CanGoForward);
        Lit(reload, tabs.Live is { IsBlank: false, IsOwn: false });
        Bookmarked();
        Title = tabs.Live is { IsBlank: false } live ? live.Label : Store.Name;
        bar.Show(tabs.Live);
        offer.Show(tabs.Live);
        find.Follow(tabs.Live);

        // Пустая вкладка — это и есть поле: в ней больше нечего показывать.
        if (tabs.Live is { IsBlank: true })
        {
            if (!field.IsOpen) field.Open("");
        }
        else if (field.IsOpen) field.Close();
    }

    // Строка встаёт на своё место в колонке; уже стоящая там не трогается.
    void Seat(UIElement row, int place)
    {
        if (place < rows.Children.Count && rows.Children[place] == row) return;
        rows.Children.Remove(row);
        rows.Children.Insert(place, row);
    }

    /// Строку несут указателем на travel точек по высоте; done — отпустили.
    /// Строки на ходу не переставляются: вынутая из колонки строка теряет
    /// захват указателя. Пока несут, строки только сдвинуты, а вкладка
    /// переезжает, когда её отпустят (Tabs.Place). Ряд у закреплённых свой.
    void Carry(Tab tab, double travel, bool done)
    {
        var from = tabs.All.IndexOf(tab);
        if (from < 0 || !shown.TryGetValue(tab.Id, out var carried))
        {
            slot.Visibility = Visibility.Collapsed;
            return;
        }
        var pins = tabs.All.Count(other => other.Pin is not null);
        var (first, last) = tab.Pin is null ? (pins, tabs.All.Count - 1) : (0, pins - 1);
        var step = Metrics.Row + Metrics.TabGap;
        // Дальше своего ряда строка не уходит.
        travel = Math.Clamp(travel, (first - from) * step, (last - from) * step);
        var to = from + (int)Math.Round(travel / step);
        for (var i = first; i <= last; i++)
        {
            if (!shown.TryGetValue(tabs.All[i].Id, out var row)) continue;
            var y = done ? 0 : i == from ? travel : i > from && i <= to ? -step : i < from && i >= to ? step : 0;
            row.Shift(y, !done && i == from);
        }
        // Место строки в раскладке (без её сдвига) плюс места, которые она прошла.
        slot.Translation = new System.Numerics.Vector3(0, (float)(carried.ActualOffset.Y + (to - from) * step), 0);
        slot.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        if (done) tabs.Place(tab, to);
    }

    /// Адрес страницы — в буфер обмена (copyAddress апстрима).
    // ponytail: апстрим ещё говорит «Address copied»; у нас строки для таких слов нет.
    static void CopyAddress(Tab tab)
    {
        if (tab.IsBlank || tab.IsOwn) return;
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(tab.Url);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
    }

    /// Ctrl+Shift+V, когда ничего не набирается (pasteAndGo апстрима): текст из
    /// буфера обмена — адрес или слова для поиска — в живой вкладке.
    async void PasteAndGo()
    {
        // В тест-канале буфер обмена — человека за этим компьютером: его не
        // читаем, текст даёт проверка (clip), иначе он ушёл бы поисковику.
        var text = clip;
        if (!Store.Testing)
        {
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
                text = await content.GetTextAsync();
            }
            // Буфер обмена держит другая программа.
            catch (System.Runtime.InteropServices.COMException) { return; }
        }
        if (Tabs.Destination(text) is not { } destination) return;
        field.Close();
        tabs.Go(destination);
    }

    /// Закрыть вкладку со строки: крестик, средняя кнопка, Delete.
    void Drop(Tab tab)
    {
        // Фокус с клавиатуры стоял на этой строке (Delete) — он перейдёт на
        // соседнюю, а не потеряется. Решаем до закрытия: закрытие живой вкладки
        // сразу отдаёт фокус странице соседки (Tabs.Select).
        var held = shown[tab.Id].Focused ? tabs.All.IndexOf(tab) : -1;
        tabs.Close(tab);
        if (held >= 0 && tabs.All.Count > 0 && shown.TryGetValue(tabs.All[Math.Min(held, tabs.All.Count - 1)].Id, out var next)) next.Take();
    }

    // Нажатие на живую вкладку правит её адрес, на другую — переключает.
    void Pick(Tab tab)
    {
        if (tab == tabs.Live) EditAddress();
        else tabs.Select(tab);
    }

    void EditAddress()
    {
        if (tabs.Live is { } live) field.Open(live.Editable);
    }

    /// Светлое окно, тёмное или как в Windows: ключ «look», как в апстриме.
    void Look() => root.RequestedTheme = Settings.Text("look") switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    void Theme()
    {
        var dark = root.ActualTheme == ElementTheme.Dark;
        Palette.Apply(dark);
        tabs.Look(dark);
        field.Retint();
        // Кнопки окна рисует система: им нужны наши цвета, иначе в тёмной теме
        // они остаются чёрными на тёмном.
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = bar.ButtonHoverForegroundColor = bar.ButtonPressedForegroundColor = Palette.Ink.Color;
        bar.ButtonInactiveForegroundColor = Palette.Muted.Color;
        bar.ButtonHoverBackgroundColor = Palette.SideHover.Color;
        bar.ButtonPressedBackgroundColor = Palette.SideWash.Color;
    }

    /// Меньше Metrics.WindowWidth на WindowHeight окно не сжимается: ни рамкой,
    /// ни AppWindow.Resize. Предел у презентера в пикселях экрана, поэтому
    /// пересчитывается при смене масштаба — окно переехало на другой экран.
    /// Window.MinWidth, который делает это сам, в Windows App SDK 2.7 ещё нет.
    void Least()
    {
        if (root.XamlRoot is not { } xaml || AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var width = (int)Math.Ceiling(Metrics.WindowWidth * xaml.RasterizationScale);
        var height = (int)Math.Ceiling(Metrics.WindowHeight * xaml.RasterizationScale);
        if (presenter.PreferredMinimumWidth == width && presenter.PreferredMinimumHeight == height) return;
        presenter.PreferredMinimumWidth = width;
        presenter.PreferredMinimumHeight = height;
        // Прошлая рамка (window.frame) встаёт до того, как масштаб известен:
        // окно, закрытое узким до этого предела, дорастает до него.
        var size = AppWindow.Size;
        if (presenter.State == OverlappedPresenterState.Restored && (size.Width < width || size.Height < height))
            AppWindow.Resize(new SizeInt32(Math.Max(size.Width, width), Math.Max(size.Height, height)));
    }

    // Полоса целиком отдана системе под перетаскивание окна, поэтому место
    // под кнопками и строкой надо у неё забрать обратно, иначе нажатия до них
    // не дойдут.
    void Passthrough()
    {
        // Спрятанной полосе (режим фокуса) перетаскивание не возвращаем: его снял Focus.Compose.
        if (root.XamlRoot is not { } xaml || band.Visibility == Visibility.Collapsed || doors.ActualWidth == 0) return;
        var scale = xaml.RasterizationScale;
        // Кнопки окна система рисует от самого верха окна, а наша полоса
        // начинается под его краем в один пиксель: поднятые на этот пиксель,
        // значки полосы стоят на одной линии с кнопками окна (просьба владельца
        // 2026-11-20: «лайн не ровный»). Нижнее поле 2 пикселя — центр выше на 1.
        var lift = 2 / scale;
        var start = new Thickness(Metrics.SidePadding, 0, 0, lift);
        if (!doors.Margin.Equals(start)) doors.Margin = start;
        // Строка посередине карточки и не заходит ни под кнопки слева (колонка
        // сложена — карточка начинается под ними), ни под кнопки окна справа.
        var right = AppWindow.TitleBar.RightInset / scale + 8;
        if (actions.View.Margin.Right != right || actions.View.Margin.Bottom != lift) actions.View.Margin = new Thickness(0, 0, right, lift);
        var left = Math.Max(8, doors.Margin.Left + doors.ActualWidth + 8 - root.ColumnDefinitions[0].ActualWidth);
        var after = right + (actions.View.ActualWidth > 0 ? actions.View.ActualWidth + 8 : 0);
        var inset = Math.Max(left, after);
        // Узкое окно: строке не хватает до BarNarrowest — ей отдаётся отступ с
        // той стороны, где его не требуют кнопки, и она съезжает с середины.
        var lack = Math.Max(0, Metrics.BarNarrowest - (root.ColumnDefinitions[1].ActualWidth - 2 * inset));
        var place = new Thickness(inset - Math.Min(lack, inset - left), 0, inset - Math.Min(lack, inset - after), lift);
        if (!bar.View.Margin.Equals(place)) bar.View.Margin = place;
        RectInt32 Hole(FrameworkElement part)
        {
            var corner = part.TransformToVisual(null).TransformPoint(new Point(0, 0));
            return new RectInt32((int)(corner.X * scale), (int)(corner.Y * scale),
                (int)Math.Ceiling(part.ActualWidth * scale), (int)Math.Ceiling(part.ActualHeight * scale));
        }
        RectInt32[] now = actions.View.ActualWidth > 0 ? [Hole(doors), Hole(bar.View), Hole(actions.View)] : [Hole(doors), Hole(bar.View)];
        if (now.SequenceEqual(holes)) return;
        holes = now;
        var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        source.SetRegionRects(NonClientRegionKind.Passthrough, now);
        // Таскается вся полоса. Обычно это ставит SetTitleBar, но после режима
        // фокуса (Focus.Compose снимает перетаскивание) он его не возвращает:
        // остаётся системный заголовок в 32 точки до кнопок окна.
        source.SetRegionRects(NonClientRegionKind.Caption, [Hole(band)]);
    }

    void Bindings()
    {
        // Что делает команда; её сочетание — в Shortcuts (Settings › Keyboard
        // Shortcuts), человек может его сменить. Ctrl+, — как ⌘, на Mac;
        // Ctrl+H — как в браузерах на Windows (на Mac ⌘Y); Ctrl+Shift+F — как
        // «компактный режим» Google Docs.
        acts = new Dictionary<string, Action>
        {
            ["app.settings"] = () => tabs.Show(Own.Settings),
            ["file.newTab"] = () => tabs.Open(null),
            ["file.newPrivateTab"] = () => tabs.OpenPrivate(),
            ["file.reopen"] = () => tabs.Reopen(),
            ["file.openAddress"] = EditAddress,
            ["file.closeTab"] = () => { if (tabs.Live is { } live) tabs.Close(live); },
            ["file.print"] = tabs.Print,
            ["edit.find"] = find.Open,
            ["edit.findNext"] = () => find.Look(true),
            ["edit.findPrevious"] = () => find.Look(false),
            ["view.fold"] = ToggleFold,
            ["view.focus"] = ToggleFocus,
            ["view.reload"] = tabs.Reload,
            ["view.reloadOrigin"] = tabs.ReloadOrigin,
            ["view.inspector"] = tabs.Inspect,
            ["tabs.back"] = tabs.Back,
            ["tabs.forward"] = tabs.Forward,
            ["tabs.next"] = () => tabs.Step(1),
            ["tabs.previous"] = () => tabs.Step(-1),
            ["tabs.duplicate"] = () => { if (tabs.Live is { } live) tabs.Duplicate(live); },
            ["tabs.copyAddress"] = () => { if (tabs.Live is { } live) CopyAddress(live); },
            ["tabs.pasteAndGo"] = PasteAndGo,
            ["bookmarks.add"] = () => { if (Keepable() is { } page) Bookmarks.Add(page, tabs.Live!.Title); },
            ["history.show"] = () => tabs.Show(Own.History),
            ["history.clearData"] = () =>
            {
                // Как clearBrowsingData апстрима: история, сразу с карточкой очистки.
                tabs.Show(Own.History);
                if (tabs.Live?.Sheet is HistoryPage history) history.Clearing(true);
            },
            ["history.downloads"] = () => tabs.Show(Own.Downloads),
        };
        // Сочетания, которые приходят и из страницы (PageKeys): по имени.
        var fromPage = new Dictionary<string, Action>();
        (string? Name, long At) last = default;
        void On(Combo combo, Action act, bool typing = false)
        {
            var with = (combo.Ctrl ? VirtualKeyModifiers.Control : 0) | (combo.Shift ? VirtualKeyModifiers.Shift : 0)
                | (combo.Alt ? VirtualKeyModifiers.Menu : 0);
            var shortcut = new KeyboardAccelerator { Key = (VirtualKey)combo.Code, Modifiers = with };
            shortcut.Invoked += (_, pressed) =>
            {
                // Пока в настройках ждут новое сочетание, нажатие — ему, а не команде.
                if (SettingsPage.Recording) return;
                // Command.Typing: в поле окна нажатие остаётся полю; в странице
                // решает её скрипт (PageKeys) — набирают ли там что-то.
                if (typing && FocusManager.GetFocusedElement(root.XamlRoot) is TextBox or WebView2) return;
                pressed.Handled = true;
                last = (combo.Name, Environment.TickCount64);
                act();
            };
            root.KeyboardAccelerators.Add(shortcut);
            fromPage[combo.Name] = act;
        }
        void Bind()
        {
            root.KeyboardAccelerators.Clear();
            fromPage.Clear();
            foreach (var command in Shortcuts.All)
                if (Shortcuts.Key(command.Id) is { } combo) On(combo, acts[command.Id], command.Typing);
            // Вторые клавиши, как в браузерах на Windows. Не меняются: Shortcuts
            // их никому не отдаёт. Те, что движок понимает сам внутри страницы
            // (F5, F12, Ctrl+P…), здесь работают и из адресной строки с колонкой.
            On(new Combo("f5"), tabs.Reload);
            On(new Combo("f5", Ctrl: true), tabs.ReloadOrigin);
            On(new Combo("f12"), tabs.Inspect);
            for (var n = 1; n <= 9; n++)
            {
                var number = n;
                On(Shortcuts.Numbers[number - 1], () => tabs.Number(number));
            }
        }
        tabs.Pressed += name =>
        {
            // Если элемент WebView2 всё же отдал окну то же нажатие и оно уже
            // сработало, сообщение страницы о нём второй раз ничего не делает:
            // иначе Ctrl+W закрыл бы две вкладки.
            if (last.Name == name && Environment.TickCount64 - last.At < 300) return;
            if (fromPage.TryGetValue(name, out var act)) act();
        };
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Bind();
        Settings.Changed += key =>
        {
            if (key != "shortcuts") return;
            Bind();
            tabs.Rekey();
        };
    }

    /// Кнопка в полосе: Door из Side.swift. Плашка — содержимое настоящей
    /// кнопки: от неё фокус, нажатие с клавиатуры и имя для экранного диктора.
    /// Щелчок мышью фокус не берёт: он остаётся на странице.
    internal static Button Door(string glyph, string name, Action act)
    {
        var icon = Ui.Glyph(glyph, 15, Palette.Muted);
        var plate = new Border
        {
            Width = Metrics.Door, Height = Metrics.Door, CornerRadius = new CornerRadius(Metrics.DoorRadius),
            Background = Palette.Clear, Child = icon,
        };
        var door = Ui.Bare(new Button { CornerRadius = new CornerRadius(Metrics.DoorRadius), AllowFocusOnInteraction = false }, name, plate);
        door.PointerEntered += (_, _) =>
        {
            if (!door.IsEnabled) return;
            plate.Background = Palette.SideHover;
            icon.Stroke = Palette.InkSoft;
        };
        door.PointerExited += (_, _) =>
        {
            plate.Background = Palette.Clear;
            icon.Stroke = door.IsEnabled ? Palette.Muted : Palette.Faint;
        };
        door.Click += (_, _) => act();
        return door;
    }

    // Кнопка, которой сейчас нечего делать, гаснет и не отзывается: выключенная,
    // она не берёт ни фокус, ни нажатия, и диктор читает её недоступной.
    /// Страница, которую можно сохранить закладкой: сайт, а не пустая
    /// вкладка и не своя страница браузера.
    Uri? Keepable() => tabs.Live is { IsBlank: false, IsOwn: false, Page: { Scheme: "http" or "https" } page } ? page : null;

    /// Кнопка-закладка залита на сохранённой странице (BookmarkDoor апстрима).
    // ponytail: щелчок только сохраняет и убирает. Карточки с именем и папкой
    // (BookmarkCard) и списка под кнопкой нет; список — на странице Bookmarks.
    void Bookmarked()
    {
        var page = Keepable();
        var kept = page is not null && Bookmarks.Find(page) is not null;
        Lit(bookmark, page is not null);
        ((Icon)((Border)bookmark.Content).Child).Fill = kept ? Palette.Muted : null;
        AutomationProperties.SetName(bookmark, kept ? "Remove bookmark" : "Bookmark this page");
    }

    void ToggleBookmark()
    {
        if (Keepable() is not { } page) return;
        if (Bookmarks.Find(page) is { } kept) Bookmarks.Remove(kept.Id);
        else Bookmarks.Add(page, tabs.Live!.Title);
    }

    static void Lit(Button door, bool on)
    {
        door.IsEnabled = on;
        var plate = (Border)door.Content;
        plate.Background = Palette.Clear;
        ((Icon)plate.Child).Stroke = on ? Palette.Muted : Palette.Faint;
    }

    /// Строка-действие, а не страница: тихая, пока на неё не навели. Quiet из Side.swift.
    /// Содержимое — строка, нажатие и фокус — кнопка поверх неё.
    static Button Quiet(string glyph, string title, Action act)
    {
        var icon = Ui.Glyph(glyph, 14, Palette.Faint);
        icon.Width = 15;
        icon.Margin = new Thickness(0, 0, 8, 0);
        // Посередине строки — буквы, а не строка шрифта (TextLineBounds.Tight): иначе
        // подпись стояла на полторы-две точки ниже середины значка (замер по снимку).
        var text = new TextBlock
        {
            Text = title, FontSize = 12.5, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center,
            TextLineBounds = TextLineBounds.Tight,
        };
        var row = new Grid
        {
            Height = Metrics.Row, CornerRadius = new CornerRadius(Metrics.RowRadius), Background = Palette.Clear,
            Padding = new Thickness(10, 0, 10, 0),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(text, 1);
        row.Children.Add(icon);
        row.Children.Add(text);
        var button = Ui.Bare(new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(Metrics.RowRadius),
            AllowFocusOnInteraction = false,
        }, title, row);
        button.PointerEntered += (_, _) =>
        {
            row.Background = Palette.SideHover;
            icon.Stroke = text.Foreground = Palette.InkSoft;
        };
        button.PointerExited += (_, _) =>
        {
            row.Background = Palette.Clear;
            icon.Stroke = text.Foreground = Palette.Faint;
        };
        button.Click += (_, _) => act();
        return button;
    }
}
