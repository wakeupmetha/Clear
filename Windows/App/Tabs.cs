using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Browser;

/// Одна страница. Её движок создаётся лениво: вкладка из прошлой сессии не
/// стоит ни одного процесса, пока на неё не переключились. Поэтому запуск с
/// двадцатью вкладками мгновенный.
sealed class Tab
{
    public readonly Guid Id = Guid.NewGuid();
    /// Пусто у новой вкладки, пока в ней никуда не пошли.
    public string Url = "";
    public string Title = "";
    /// Эти поля пока только проходят через сессию: закрепление ещё не нарисовано.
    public string? Pin, Name, Home;
    public bool Muted;
    public DateTime? Touched;
    public bool Loading;
    /// Последняя навигация удалась. Заголовок страницы об ошибке — не заголовок
    /// сайта, поэтому в историю он не пишется.
    public bool Landed;
    /// Null, пока страница спит.
    public WebView2? View;
    /// Своя страница браузера (настройки, история) вместо движка: её рисует оболочка.
    public FrameworkElement? Sheet;
    /// Чем вкладка показана: движком или своей страницей.
    public FrameworkElement? Shown => (FrameworkElement?)View ?? Sheet;
    /// Куда вкладка сейчас идёт сама: блокировщик такой запрос не трогает.
    public string Heading = "";
    /// Скрипт блокировщика, поставленный на движок этой вкладки.
    public string? ShieldScript;
    /// Скрипт сочетаний (PageKeys), поставленный на движок этой вкладки.
    public string? KeyScript;

    /// Что-то изменилось в самой вкладке: заголовок, адрес, загрузка.
    public event Action? Changed;
    public void Notify() => Changed?.Invoke();

    public bool IsBlank => Url.Length == 0;
    /// Адрес ведёт на свою страницу браузера, а не на сайт.
    public bool IsOwn => Page is { } page && Own.Is(page);

    public Uri? Page => Uri.TryCreate(Url, UriKind.Absolute, out var page) ? page : null;

    public string Label =>
        Name ?? (Title.Length > 0 ? Title
            : IsBlank ? "New tab"
            : Page is { } page ? Own.Title(page) ?? Address.Pretty(page)
            : Url);

    /// Буква сайта: первая буква хоста без www.
    public string Monogram =>
        Page is { } page && Address.WithoutWWW(page.Host) is { Length: > 0 } host ? host[..1].ToUpperInvariant() : "";

    /// Адрес, как его показывает поле для правки.
    public string Editable => Page is { } page ? Address.Editable(page) : Url;
}

/// Вкладки одного окна: какие открыты, какая перед глазами, и всё, что с ними
/// делают. Сохраняет сессию и пишет историю.
sealed class Tabs(Panel stage)
{
    public readonly List<Tab> All = [];
    public Tab? Live { get; private set; }
    public readonly History History = new();

    /// Изменился список или вкладка перед глазами.
    public event Action? Changed;
    /// Закрыли последнюю пустую вкладку: окну больше нечего показывать.
    public event Action? Emptied;
    /// В живой странице нажато сочетание окна: его имя из PageKeys.
    public event Action<string>? Pressed;

    /// Подпись сообщений скрипта сочетаний. Живёт сеанс и известна только
    /// окну и скрипту: страница не может прислать сочетание от себя.
    static readonly string KeyToken = Guid.NewGuid().ToString("N");

    /// Одна среда движка на все вкладки. Расширения включаются у среды, а
    /// второй движок на той же папке с другими настройками не запустится.
    /// Папку данных и ключи движка задают переменные окружения (App).
    static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment>? shared;
    public static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> Shared() =>
        shared ??= Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, null,
            new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true }).AsTask();

    /// uBlock ищется и ставится один раз за сеанс, первым проснувшимся движком.
    static Task? ublock;

    /// Профиль движка для расширений и очистки: у любой проснувшейся вкладки он один.
    public Microsoft.Web.WebView2.Core.CoreWebView2Profile? Profile =>
        All.Select(tab => tab.View?.CoreWebView2).OfType<Microsoft.Web.WebView2.Core.CoreWebView2>().FirstOrDefault()?.Profile;

    /// Где были до нынешней вкладки: туда возвращаемся, закрыв пустую.
    Tab? previous;

    /// Куда ведёт набранное: файл, адрес или слова для поисковика.
    public static Uri? Destination(string typed) =>
        Address.File(typed)
        ?? (Address.Url(typed) is { } url ? Address.Reachable(url) ?? url : null)
        ?? Engines.Url(typed, Engines.Current);

    public void Restore()
    {
        var shape = Session.Read();
        foreach (var entry in shape.Tabs)
        {
            All.Add(new Tab
            {
                Url = entry.Url, Title = entry.Title, Pin = entry.Pin, Name = entry.Name, Home = entry.Home,
                Touched = entry.Touched, Muted = entry.Muted,
            });
        }
        if (shape.Active >= 0 && shape.Active < All.Count) Select(All[shape.Active]);
        else Open(null);
    }

    public Tab Open(Uri? url, bool select = true)
    {
        // Пустая вкладка одна: Ctrl+T и «New tab» при уже открытой пустой ведут
        // в неё, а не копят ряд одинаковых (как newTab в Browser.swift).
        if (url is null && All.LastOrDefault(tab => tab.IsBlank) is { } blank)
        {
            if (select) Select(blank);
            // Поле в пустой вкладке открывается заново, даже если она уже живая.
            Changed?.Invoke();
            return blank;
        }
        var tab = new Tab { Url = url?.AbsoluteUri ?? "" };
        All.Add(tab);
        if (select) Select(tab);
        else
        {
            Save();
            Changed?.Invoke();
        }
        return tab;
    }

    /// В живую вкладку, а если её нет — в новую.
    public void Go(Uri url)
    {
        // Своя страница в окне одна: если она уже открыта, идём к ней, а
        // пустую вкладку, из которой о ней попросили, за собой закрываем.
        if (Own.Is(url) && All.FirstOrDefault(tab => tab.Url == url.AbsoluteUri) is { } there)
        {
            var from = Live;
            Select(there);
            if (from is { IsBlank: true }) Close(from);
            return;
        }
        if (Live is not { } tab)
        {
            Open(url);
            return;
        }
        tab.Url = url.AbsoluteUri;
        tab.Title = "";
        // Сайт показывает движок, свою страницу — оболочка. При переходе от
        // одного к другому, и от одной своей страницы к другой, прежнее убирается.
        if (tab.Sheet is not null || (tab.IsOwn && tab.View is not null)) Strip(tab);
        // Сразу, не дожидаясь движка: кольцо на вкладке появляется по нажатию Enter.
        tab.Loading = !tab.IsOwn;
        if (tab.Shown is null) Wake(tab);
        else tab.View?.CoreWebView2?.Navigate(tab.Url);   // если движок ещё не готов, Wake пойдёт по tab.Url сам
        tab.View?.Focus(FocusState.Programmatic);
        tab.Notify();
        Save();
        Changed?.Invoke();
    }

    /// Своя страница по кнопке или сочетанию клавиш: к уже открытой, в пустую
    /// вкладку или в новую, но не поверх страницы, которую читают.
    public void Show(Uri page)
    {
        if (Live is { IsBlank: true } || All.Any(tab => tab.Url == page.AbsoluteUri)) Go(page);
        else Open(page);
    }

    /// Убирает то, чем вкладка показана. Движок при этом действительно
    /// уходит: его процесс отдаётся.
    void Strip(Tab tab)
    {
        if (tab.View is { } view)
        {
            // Сначала забыть, потом закрыть. Close, не возвращаясь, продолжает
            // Wake, который ждал запуска этого движка, и тот должен увидеть,
            // что вкладка уже не его. В обратном порядке окно падало, если
            // вкладку закрывали сразу после открытия.
            tab.View = null;
            stage.Children.Remove(view);
            view.Close();
        }
        if (tab.Sheet is { } sheet)
        {
            stage.Children.Remove(sheet);
            tab.Sheet = null;
        }
    }

    public void Select(Tab tab)
    {
        if (Live == tab) return;
        if (Live is { } left)
        {
            left.Touched = DateTime.UtcNow;
            previous = left;
        }
        Live = tab;
        Wake(tab);
        foreach (var other in All)
            if (other.Shown is { } shown) shown.Visibility = other == tab ? Visibility.Visible : Visibility.Collapsed;
        tab.View?.Focus(FocusState.Programmatic);
        Save();
        Changed?.Invoke();
    }

    public void Close(Tab tab)
    {
        var index = All.IndexOf(tab);
        if (index < 0) return;
        All.RemoveAt(index);
        Strip(tab);
        if (previous == tab) previous = null;

        if (Live == tab)
        {
            Live = null;
            if (All.Count == 0)
            {
                // Единственная вкладка: пустая закрывает окно, страница
                // оставляет после себя пустую вкладку, как в апстриме.
                if (tab.IsBlank)
                {
                    Save(now: true);
                    Emptied?.Invoke();
                    return;
                }
                Open(null);
                return;
            }
            // С пустой вкладки возвращаемся туда, где были; иначе на соседку,
            // вставшую на её место.
            var next = tab.IsBlank && previous is not null && All.Contains(previous)
                ? previous
                : All[Math.Min(index, All.Count - 1)];
            Select(next);
            return;
        }
        Save();
        Changed?.Invoke();
    }

    public void Step(int by)
    {
        if (Live is null || All.Count < 2) return;
        Select(All[((All.IndexOf(Live) + by) % All.Count + All.Count) % All.Count]);
    }

    public bool CanGoBack => Live?.View?.CoreWebView2?.CanGoBack == true;
    public bool CanGoForward => Live?.View?.CoreWebView2?.CanGoForward == true;
    public void Back() { if (CanGoBack) Live!.View!.CoreWebView2.GoBack(); }
    public void Forward() { if (CanGoForward) Live!.View!.CoreWebView2.GoForward(); }
    public void Reload() => Live?.View?.CoreWebView2?.Reload();

    // Печать, DevTools и список загрузок движок открывает сам по своим клавишам,
    // но только пока фокус внутри страницы. Из адресной строки и колонки вкладок
    // эти клавиши до движка не доходят, поэтому оболочка берёт их на себя.
    // Browser: диалог печати самого браузера, как у Ctrl+P в движке (второй вид — системный).
    public void Print() => Live?.View?.CoreWebView2?.ShowPrintUI(Microsoft.Web.WebView2.Core.CoreWebView2PrintDialogKind.Browser);
    public void Inspect() => Live?.View?.CoreWebView2?.OpenDevToolsWindow();

    public bool Downloads()
    {
        if (Live?.View?.CoreWebView2 is not { } engine) return false;
        // Свойство движка меняется не сразу после вызова: отвечаем тем, что попросили.
        var open = !engine.IsDefaultDownloadDialogOpen;
        if (open) engine.OpenDefaultDownloadDialog();
        else engine.CloseDefaultDownloadDialog();
        return open;
    }

    bool dark;

    /// Светлое окно или тёмное: страницы следуют за окном, а не за системой.
    /// Иначе под светлым окном лежит тёмная страница, и наоборот.
    public void Look(bool dark)
    {
        this.dark = dark;
        Redress();
    }

    void Dress(WebView2 view)
    {
        // Фон движка — фон карточки: без белой вспышки в тёмной теме.
        view.DefaultBackgroundColor = Palette.Ground.Color;
        if (view.CoreWebView2 is not { } core) return;
        var profile = core.Profile;
        profile.PreferredColorScheme = dark
            ? Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Dark
            : Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Light;
        // Настройки движка через его API, а не флагами (решение владельца
        // 2026-11-21). Ключи апстрима: sites.keep — «Prevent cross-site
        // tracking», выключенный; включённый — уровень движка по умолчанию.
        profile.PreferredTrackingPreventionLevel = Settings.Bool("sites.keep")
            ? Microsoft.Web.WebView2.Core.CoreWebView2TrackingPreventionLevel.None
            : Microsoft.Web.WebView2.Core.CoreWebView2TrackingPreventionLevel.Balanced;
        // passwords.save включён по умолчанию, как в апстриме (у движка — нет).
        profile.IsPasswordAutosaveEnabled = Settings.Bool("passwords.save", true);
        // contacts.fill в апстриме выключен, потому что macOS спрашивает про
        // Контакты; здесь спрашивать некого, и движок по умолчанию заполняет.
        profile.IsGeneralAutofillEnabled = Settings.Bool("contacts.fill", true);
    }

    /// Настройку движка сменили: применить ко всем живым движкам. Профиль у
    /// них один, но спящие вкладки получат её сами при пробуждении.
    public void Redress()
    {
        foreach (var tab in All)
            if (tab.View is { } view) Dress(view);
    }

    /// now — при выходе: сессия, отданная фоновой очереди, может не дойти до диска.
    public void Save(bool now = false)
    {
        // Пустая вкладка — не место, куда возвращаются.
        var kept = All.Where(tab => !tab.IsBlank).ToList();
        Session.Write(new Session.Shape
        {
            Tabs = kept.Select(tab => new Session.Entry
            {
                Url = tab.Url, Title = tab.Title, Pin = tab.Pin, Name = tab.Name, Home = tab.Home,
                Touched = tab.Touched, Muted = tab.Muted,
            }).ToList(),
            Active = Live is null ? -1 : kept.IndexOf(Live),
        }, now);
    }

    /// Создаёт движок страницы, когда она впервые понадобилась.
    async void Wake(Tab tab)
    {
        if (tab.Shown is not null || tab.IsBlank) return;
        if (tab.IsOwn)
        {
            // Своей странице движок не нужен.
            tab.Loading = false;
            tab.Sheet = tab.Url == Own.History.AbsoluteUri ? new HistoryPage(this) : new SettingsPage(this);
            stage.Children.Add(tab.Sheet);
            return;
        }
        var view = new WebView2();
        Dress(view);
        tab.View = view;
        tab.Loading = true;
        stage.Children.Add(view);
        try
        {
            await view.EnsureCoreWebView2Async(await Shared());
        }
        catch (Exception e)
        {
            // Нет рантайма WebView2 или он не запустился: говорим об этом на
            // вкладке, а не падаем всем окном.
            tab.Loading = false;
            tab.Title = "WebView2 is not available: " + e.Message;
            tab.Notify();
            return;
        }
        // Вкладку закрыли, пока движок запускался, или он не запустился молча.
        if (tab.View != view || view.CoreWebView2 is not { } core) return;

        Dress(view);
        // Переход, начатый последним: конец прежнего, отменённого новым, вкладку не трогает.
        ulong going = 0;
        core.NavigationStarting += (_, start) =>
        {
            going = start.NavigationId;
            tab.Landed = false;
            tab.Heading = Blocking.Plain(start.Uri);
            tab.Loading = true;
            tab.Notify();
        };
        core.NavigationCompleted += (_, done) =>
        {
            tab.Landed = done.IsSuccess;
            tab.Loading = false;
            if (done.IsSuccess)
            {
                // Заголовок берём и здесь: у новой страницы с тем же
                // заголовком, что у прежней, событие о его смене не приходит.
                tab.Title = Titled(core);
                if (Uri.TryCreate(core.Source, UriKind.Absolute, out var page)) History.Record(page, tab.Title);
                Save();
            }
            else if (done.NavigationId == going && !Same(core.Source, tab.Url))
            {
                // Переход не состоялся: файл ушёл в загрузки или переход отменили.
                // Go уже записал новый адрес, а движок остался на прежней странице;
                // без возврата вкладка после перезапуска скачала бы файл заново.
                // У вкладки, открытой только ради файла, страницы нет: она пустая.
                // Движок ей оставляем: с ним живут загрузка и её список.
                var empty = core.Source == "about:blank";
                tab.Url = empty ? "" : core.Source;
                tab.Title = empty ? "" : Titled(core);
                tab.Landed = !empty;
                Save();
            }
            tab.Notify();
            Changed?.Invoke();
        };
        core.SourceChanged += (_, _) =>
        {
            tab.Url = core.Source;
            tab.Notify();
            Save();
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = Titled(core);
            if (tab.Landed && Uri.TryCreate(core.Source, UriKind.Absolute, out var page)) History.Retitle(page, tab.Title);
            tab.Notify();
            Save();
            if (tab == Live) Changed?.Invoke();
        };
        // Назад и вперёд зажигаются и гаснут вместе с историей страницы.
        core.HistoryChanged += (_, _) => { if (tab == Live) Changed?.Invoke(); };
        // Значок, который объявила страница, движок уже скачал: кладём его в
        // запас, и знаки сайта во вкладках, истории и подсказках надевают его.
        core.FaviconChanged += async (_, _) =>
        {
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var page) || Icons.Site(page) is not { } site
                || string.IsNullOrEmpty(core.FaviconUri)) return;
            try
            {
                using var stream = await core.GetFaviconAsync(Microsoft.Web.WebView2.Core.CoreWebView2FaviconImageFormat.Png);
                if (tab.View != view || stream is null) return;
                using var copy = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(copy);
                if (Icons.Keep(page, copy.ToArray())) Mark.Refresh(site);
            }
            // Вкладку закрыли, пока движок отдавал картинку: значок не нужен.
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException
                or ObjectDisposedException or IOException) { }
        };
        // Страница просит новое окно: окно у браузера одно, даём вкладку. Но
        // только если об этом попросил человек нажатием. Окно, которое страница
        // открывает сама, — это всплывающая реклама: его не будет.
        // В апстриме у вкладки при этом появляется знак с «Открыть»; здесь
        // пока просто отказ. Свои страницы браузера сайту не открыть вовсе:
        // туда ведёт только набранное человеком.
        core.NewWindowRequested += (_, asked) =>
        {
            asked.Handled = true;
            if (asked.IsUserInitiated && Uri.TryCreate(asked.Uri, UriKind.Absolute, out var wanted) && !Own.Is(wanted))
                Open(wanted);
        };
        // Сочетания окна, нажатые в странице: до сочетаний окна они не доходят,
        // их сообщает скрипт страницы (PageKeys). Из рамок — тоже: фокус бывает
        // в видеоплеере или редакторе внутри рамки.
        // ponytail: только рамки первого уровня (FrameCreated), и на страницах
        // движка (edge://, PDF, ошибка) скрипта нет. Мешает — CoreWebView2Frame.FrameCreated.
        void Heard(Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs said)
        {
            string text;
            try { text = said.TryGetWebMessageAsString(); }
            catch (ArgumentException) { return; }   // не строка — не наше
            if (tab == Live && PageKeys.Read(text, KeyToken) is { } key) Pressed?.Invoke(key);
        }
        // Ctrl+W в странице движок делает сам (раз мы принимаем NewWindowRequested)
        // и просит закрыть окно; так же — window.close() страницы, которой Chrome
        // разрешил бы закрыться: открытой самой страницей или с одной записью в
        // истории. Окно у нас — вкладка.
        core.WindowCloseRequested += (_, _) => { if (tab.View == view) Close(tab); };
        core.WebMessageReceived += (_, said) => Heard(said);
        core.FrameCreated += (_, made) => made.Frame.WebMessageReceived += (_, said) => Heard(said);
        await Rekey(tab, core);
        if (tab.View != view) return;
        await (ublock ??= UBlock.Ensure(core.Profile));
        if (tab.View != view) return;
        // Защита ставится до первой загрузки, а не вдогонку ей.
        await Blocking.Protect(tab, core);
        if (tab.View != view) return;
        core.Navigate(tab.Url);
        // Проснувшаяся страница не отбирает фокус с клавиатуры у колонки: после
        // Delete на строке живой вкладки он стоит на соседней строке (MainWindow.Drop).
        if (tab == Live && (view.XamlRoot is null
            || FocusManager.GetFocusedElement(view.XamlRoot) is not Control { FocusState: FocusState.Keyboard }))
            view.Focus(FocusState.Programmatic);
    }

    /// Сочетания поменяли: скрипт сочетаний ставится заново на все движки.
    // ponytail: открытая страница узнаёт новые сочетания со следующей загрузки,
    // а до неё ловит прежние (окно их уже не примет). Мешает — слать список в
    // страницу сообщением.
    public void Rekey()
    {
        foreach (var tab in All)
            if (tab.View?.CoreWebView2 is { } core) _ = Rekey(tab, core);
    }

    static async Task Rekey(Tab tab, Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        if (tab.KeyScript is { } old) core.RemoveScriptToExecuteOnDocumentCreated(old);
        tab.KeyScript = null;
        tab.KeyScript = await core.AddScriptToExecuteOnDocumentCreatedAsync(PageKeys.Script(KeyToken));
    }

    /// Блокировщик включили, выключили или сменили сайты, где он выключен:
    /// открытые страницы узнают об этом сразу, а не при следующем запуске.
    public void Reshield()
    {
        Microsoft.Web.WebView2.Core.CoreWebView2? any = null;
        foreach (var tab in All)
        {
            if (tab.View?.CoreWebView2 is not { } core) continue;
            _ = Blocking.Dress(tab, core);
            any = core;
        }
        // Движок спрашивает оболочку только о запросах, которые идут в сеть.
        // Скрипт, попавший в кэш, пока блокировщик был выключен, страница
        // взяла бы оттуда, никого не спросив. Кэш у всех вкладок общий, поэтому
        // сбрасываем его один раз: «включено» должно значить «включено».
        _ = any?.Profile.ClearBrowsingDataAsync(Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.DiskCache);
    }

    /// Стирает данные, общие для всех вкладок (куки и входы, кэш).
    public Task Sweep(Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds kinds) =>
        WithProfile(profile => profile.ClearBrowsingDataAsync(kinds).AsTask());

    /// Дело к профилю движка: данные сайтов, расширения. Профиль один на окно,
    /// поэтому годится любой живой движок. Если ни одна вкладка не проснулась
    /// (открыты только настройки), на время дела поднимаем невидимый движок и
    /// сразу его закрываем.
    public Task WithProfile(Func<Microsoft.Web.WebView2.Core.CoreWebView2Profile, Task> act) => WithEngine(core => act(core.Profile));

    /// То же с самим движком: отладочный протокол (стереть хранилища одного
    /// сайта) есть только у него. Хранилища у профиля общие, поэтому сайт
    /// может быть и не тот, что открыт в этом движке.
    public async Task WithEngine(Func<Microsoft.Web.WebView2.Core.CoreWebView2, Task> act)
    {
        if (All.Select(tab => tab.View?.CoreWebView2).OfType<Microsoft.Web.WebView2.Core.CoreWebView2>().FirstOrDefault() is { } live)
        {
            await act(live);
            return;
        }
        var view = new WebView2 { Opacity = 0, IsHitTestVisible = false };
        stage.Children.Add(view);
        try
        {
            await view.EnsureCoreWebView2Async(await Shared());
            if (view.CoreWebView2 is { } temp) await act(temp);
        }
        finally
        {
            stage.Children.Remove(view);
            view.Close();
        }
    }

    // Один ли это адрес: движок и System.Uri пишут его чуть по-разному.
    static bool Same(string one, string other) =>
        Uri.TryCreate(one, UriKind.Absolute, out var first) && Uri.TryCreate(other, UriKind.Absolute, out var second) && first == second;

    // У страницы без заголовка движок отдаёт её адрес: это не заголовок.
    static string Titled(Microsoft.Web.WebView2.Core.CoreWebView2 core) =>
        core.DocumentTitle is { Length: > 0 } title && title != core.Source ? title : "";
}
