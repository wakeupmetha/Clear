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
    /// Закреплённая вкладка (Pins.swift): буква и адрес, где её закрепили.
    /// Имя пока только проходит через сессию.
    public string? Pin, Name, Home;
    /// Заглушена с её значка или меню: страница играет, звука нет (muted
    /// апстрима). Переживает сессию.
    public bool Muted;
    /// Страница сейчас играет звук (noisy апстрима): строка показывает динамик.
    public bool Noisy;
    public DateTime? Touched;
    /// Приватная (shy апстрима): имя своего профиля движка в памяти — свои
    /// куки и входы, — ни истории, ни места в сессии. Не спит и не
    /// закрепляется: с последним движком профиль пропадает.
    public string? Shy;
    public bool Private => Shy is not null;
    public bool Loading;
    /// Последняя навигация удалась. Заголовок страницы об ошибке — не заголовок
    /// сайта, поэтому в историю он не пишется.
    public bool Landed;
    /// Почему не состоялся последний переход (WebErrorStatus); для тест-канала.
    public string? Failure;
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
    /// Последнее нажатие по ссылке средней кнопкой или с Ctrl (PageKeys.Clicked):
    /// новая вкладка из него — за спиной, с Shift — перед глазами.
    public (DateTime At, bool Front)? Click;
    /// Загрузки, которые идут из этой страницы: пока они есть, вкладка не спит.
    public readonly HashSet<Microsoft.Web.WebView2.Core.CoreWebView2DownloadOperation> Downloads = [];

    /// Что-то изменилось в самой вкладке: заголовок, адрес, загрузка.
    public event Action? Changed;
    public void Notify() => Changed?.Invoke();

    public bool IsBlank => Url.Length == 0;
    /// Адрес ведёт на свою страницу браузера, а не на сайт.
    public bool IsOwn => Page is { } page && Own.Is(page);

    public Uri? Page => Uri.TryCreate(Url, UriKind.Absolute, out var page) ? page : null;

    public string Label =>
        Name ?? (Title.Length > 0 ? Title
            : IsBlank ? (Private ? "New private tab" : "New tab")
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
    public Microsoft.Web.WebView2.Core.CoreWebView2Profile? Profile => Engine()?.Profile;

    /// Живой движок общего профиля: у приватной вкладки профиль свой.
    Microsoft.Web.WebView2.Core.CoreWebView2? Engine() =>
        All.Where(tab => !tab.Private).Select(tab => tab.View?.CoreWebView2).OfType<Microsoft.Web.WebView2.Core.CoreWebView2>().FirstOrDefault();

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
        doze.Tick += (_, _) => SleepIdle();
        Doze();
    }

    // Сон вкладок (Sleep.swift апстрима): вкладка, на которую давно не
    // смотрели, отдаёт движок — его процессы и память. Адрес и заголовок
    // остаются; перейдёшь на неё — проснётся и загрузит страницу заново.
    // Не спят: вкладка перед глазами, закреплённая (их кладут руками, Ctrl+W;
    // пространств, где апстрим усыпляет и их, у нас нет), загружающаяся, со
    // звуком, с загрузкой и с набранным, но не отправленным текстом.
    // ponytail: просыпается загрузкой адреса — без истории «назад», места
    // прокрутки и картинки на время загрузки, как в апстриме. Мешает —
    // хранить это перед сном.
    readonly DispatcherTimer doze = new();
    bool dozing;

    static TimeSpan? SleepWait() =>
        TabSleep.Wait(Settings.Bool("tabs.sleep", true), Settings.Text("tabs.sleep.pace"), Settings.Number("sleep.after"));

    /// Сменили, как скоро спать: смотреть с новым шагом.
    public void Doze()
    {
        doze.Stop();
        doze.Interval = TabSleep.Every(SleepWait());
        doze.Start();
    }

    async void SleepIdle()
    {
        if (dozing || SleepWait() is not { } wait) return;
        dozing = true;
        try
        {
            // Дольше всех оставленные — первыми, по одной: каждую сперва спрашиваем.
            var idle = All.Where(tab => tab != Live && tab.Pin is null && !tab.Private && tab.View?.CoreWebView2 is not null && !tab.Loading
                && TabSleep.Due(tab.Touched, DateTime.UtcNow, wait)).OrderBy(tab => tab.Touched).ToList();
            foreach (var tab in idle)
            {
                if (await Busy(tab)) continue;
                // Пока спрашивали, на вкладку могли перейти или закрыть её.
                if (tab == Live || !All.Contains(tab) || tab.Loading || !TabSleep.Due(tab.Touched, DateTime.UtcNow, wait)) continue;
                Strip(tab);
                tab.Notify();
            }
        }
        finally { dozing = false; }
    }

    /// Занята ли страница тем, что сон оборвал бы. Не ответила за 3 с — занята:
    /// лучше не усыпить, чем потерять набранное.
    static async Task<bool> Busy(Tab tab)
    {
        if (tab.View?.CoreWebView2 is not { } core) return true;
        if (core.IsDocumentPlayingAudio || tab.Downloads.Count > 0) return true;
        try
        {
            var asked = core.ExecuteScriptAsync(Unsaved).AsTask();
            if (await Task.WhenAny(asked, Task.Delay(3000)) != asked) return true;
            return await asked != "false";
        }
        // Движок ушёл, пока спрашивали.
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { return true; }
    }

    /// Набранное и не отправленное: поля, как в апстриме (Forms.swift, unsaved).
    // ponytail: апстрим считает только поля, в которые печатали; здесь — любое
    // редактируемое с текстом, поэтому редактор документа не уснёт вовсе. И
    // только сама страница, без рамок. Мешает — следить за вводом скриптом.
    const string Unsaved = """
        (() => {
          for (const el of document.querySelectorAll('textarea, input, [contenteditable]')) {
            const tag = el.tagName.toLowerCase();
            if (tag === 'input' && !['text', 'email', 'url', 'tel', 'number'].includes((el.type || 'text').toLowerCase())) continue;
            if (tag === 'input' || tag === 'textarea') { if (el.value.trim() && el.value !== el.defaultValue) return true; }
            else if (el.isContentEditable && el.textContent.trim()) return true;
          }
          return false;
        })()
        """;

    /// shy — профиль приватной вкладки, из которой открыли ссылку: новая
    /// вкладка тоже приватная и входы у неё те же, как в апстриме.
    public Tab Open(Uri? url, bool select = true, string? shy = null)
    {
        // Пустая вкладка одна: Ctrl+T и «New tab» при уже открытой пустой ведут
        // в неё, а не копят ряд одинаковых (как newTab в Browser.swift).
        if (url is null && All.LastOrDefault(tab => tab.IsBlank && !tab.Private) is { } blank)
        {
            if (select) Select(blank);
            // Поле в пустой вкладке открывается заново, даже если она уже живая.
            Changed?.Invoke();
            return blank;
        }
        var tab = new Tab { Url = url?.AbsoluteUri ?? "", Shy = shy };
        All.Add(tab);
        if (select) Select(tab);
        else
        {
            Save();
            Changed?.Invoke();
        }
        return tab;
    }

    /// Ссылки, открытые за спиной из одной страницы подряд, встают за ней по
    /// порядку (linkRun апстрима). Переход на другую вкладку ряд заканчивает.
    (Tab Opener, Tab Last)? run;

    /// Вкладка для ссылки со страницы (placeForLink апстрима): рядом с открывшей,
    /// после ссылок, уже открытых из неё за спиной, не перед закреплёнными.
    /// Приватная открывает приватную, с теми же входами.
    Tab OpenLink(Uri url, Tab opener, bool front)
    {
        var here = All.IndexOf(opener);
        var anchor = run is { } open && open.Opener == opener && All.IndexOf(open.Last) is var last && last > here ? last : here;
        var tab = new Tab { Url = url.AbsoluteUri, Shy = opener.Shy };
        All.Insert(Math.Max(anchor + 1, All.Count(other => other.Pin is not null)), tab);
        if (front)
        {
            Select(tab);
            return tab;
        }
        run = (opener, tab);
        Save();
        Changed?.Invoke();
        return tab;
    }

    /// Ctrl+Shift+N (newShyTab апстрима): пустая приватная вкладка, тоже одна.
    public Tab OpenPrivate()
    {
        var tab = All.LastOrDefault(tab => tab.IsBlank && tab.Private);
        if (tab is null) All.Add(tab = new Tab { Shy = NewShy() });
        Select(tab);
        // Поле в пустой вкладке открывается заново, даже если она уже живая.
        Changed?.Invoke();
        return tab;
    }

    static string NewShy() => "private-" + Guid.NewGuid().ToString("N");

    /// Окно закрывается: приватные профили стираются (см. Strip).
    public void Forget()
    {
        foreach (var core in All.Where(tab => tab.Private).GroupBy(tab => tab.Shy)
            .Select(same => same.Select(tab => tab.View?.CoreWebView2).FirstOrDefault(core => core is not null)).OfType<Microsoft.Web.WebView2.Core.CoreWebView2>())
            core.Profile.Delete();
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
        Face(tab);
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
            tab.Noisy = false;
            // Файлы ещё идут: движок уходит со страницы, но живёт скрытым, пока
            // они не дойдут. Файл движок докачал бы и без него, но о том, что
            // дошёл, сказать было бы некому — строка в загрузках замерла бы.
            var going = tab.Downloads.ToList();
            if (going.Count > 0) Park(view, going, tab.Shy);
            else Retire(view, tab.Shy);
            // Скрипты стояли на ушедшем движке: новому их ставят заново.
            tab.KeyScript = tab.ShieldScript = null;
            tab.Downloads.Clear();
        }
        if (tab.Sheet is { } sheet)
        {
            stage.Children.Remove(sheet);
            tab.Sheet = null;
        }
    }

    /// Движки закрытых вкладок, которые ещё качают файлы, и чей приватный профиль.
    readonly Dictionary<WebView2, string?> parked = [];

    // ponytail: приостановленный файл держит движок до отмены или выхода; при
    // выходе идущие файлы обрываются без вопроса. Мешает — спросить, как Chrome.
    void Park(WebView2 view, List<Microsoft.Web.WebView2.Core.CoreWebView2DownloadOperation> going, string? shy)
    {
        view.Visibility = Visibility.Collapsed;
        parked[view] = shy;
        foreach (var download in going)
            download.StateChanged += (_, _) =>
            {
                if (!parked.ContainsKey(view) || going.Any(left => left.State == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.InProgress
                    || (left.State == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Interrupted && left.CanResume))) return;
                parked.Remove(view);
                Retire(view, shy);
            };
    }

    void Retire(WebView2 view, string? shy)
    {
        stage.Children.Remove(view);
        // Последний движок приватного профиля: папку профиля движок заводит
        // и для InPrivate — без адресов и куки, но по одной на вкладку, и
        // они копились бы. Стираем профиль; вкладка, оставшаяся без
        // движка (ушла на свою страницу), получит новый.
        if (shy is not null && view.CoreWebView2 is { } own
            && !All.Any(other => other.Shy == shy && other.View is not null) && !parked.ContainsValue(shy))
        {
            own.Profile.Delete();
            foreach (var other in All)
                if (other.Shy == shy) other.Shy = NewShy();
        }
        view.Close();
    }

    public void Select(Tab tab)
    {
        if (Live == tab) return;
        run = null;
        if (Live is { } left)
        {
            left.Touched = DateTime.UtcNow;
            previous = left;
            // Страница во весь экран выходит из него, когда уходят на другую
            // вкладку, как в Chrome: иначе окно снова встало бы во весь экран по возвращении.
            if (left.View?.CoreWebView2 is { ContainsFullScreenElement: true } full) _ = full.ExecuteScriptAsync("document.exitFullscreen()");
        }
        Live = tab;
        Wake(tab);
        foreach (var other in All)
            if (other.Shown is { } shown) shown.Visibility = other == tab ? Visibility.Visible : Visibility.Collapsed;
        Face(tab);
        Save();
        Changed?.Invoke();
    }

    /// Фокус — открытой странице: сайту — движок, своей странице — её первый
    /// элемент (у истории и закладок это поле поиска, как в апстриме). Иначе
    /// фокус спрятанной своей страницы падал на кнопку колонки с рамкой (задача 18).
    // ponytail: у пустой страницы загрузок нечего взять в фокус — он падает в
    // колонку, как раньше. Мешает — сделать саму страницу остановкой Tab.
    public void Face(Tab tab)
    {
        if (tab.View is { } view) { view.Focus(FocusState.Programmatic); return; }
        if (tab.Sheet is not { } sheet) return;
        // Только что созданная страница ещё без разметки: её элементы фокус не
        // примут до прохода разметки, а ждать Loaded нельзя — тогда она отняла
        // бы фокус у строки колонки, которую поставил Delete (MainWindow.Drop).
        if (!sheet.IsLoaded) sheet.UpdateLayout();
        if (FocusManager.FindFirstFocusableElement(sheet) is UIElement first) first.Focus(FocusState.Programmatic);
    }

    public void Close(Tab tab)
    {
        var index = All.IndexOf(tab);
        if (index < 0) return;
        if (tab.Pin is not null)
        {
            PutDown(tab);
            return;
        }
        All.RemoveAt(index);
        // Пустую и приватную не помним, как апстрим (remember в Browser.swift).
        if (!tab.IsBlank && !tab.Private)
        {
            closed.Add((tab.Url, tab.Title, index, tab.Muted));
            if (closed.Count > 12) closed.RemoveAt(0);
        }
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

    /// Закрытые вкладки, последняя в конце: Ctrl+Shift+T возвращает их по одной
    /// (ghosts апстрима). Двенадцать шагов назад, только на этот сеанс.
    // ponytail: без истории «назад» и места прокрутки (keepsake апстрима):
    // вкладка возвращается загрузкой адреса. Мешает — хранить их при закрытии.
    readonly List<(string Url, string Title, int Index, bool Muted)> closed = [];

    /// Ctrl+Shift+T: последняя закрытая — на своё место и перед глаза.
    public void Reopen()
    {
        if (closed.Count == 0) return;
        var (url, title, index, muted) = closed[^1];
        closed.RemoveAt(closed.Count - 1);
        // Своя страница в окне одна: через Show, как по её сочетанию.
        if (Uri.TryCreate(url, UriKind.Absolute, out var page) && Own.Is(page))
        {
            Show(page);
            return;
        }
        var tab = new Tab { Url = url, Title = title, Muted = muted };
        // Не перед закреплёнными: их ряд в начале колонки не двигается.
        All.Insert(Math.Clamp(index, All.Count(other => other.Pin is not null), All.Count), tab);
        Select(tab);
    }

    /// Закрепить (Browser.pin апстрима): вкладка встаёт за последней
    /// закреплённой — их буквы не двигаются под рукой; буква — первая буква
    /// сайта, дом — нынешний адрес. Пустую и свою страницу не закрепляют.
    public void Pin(Tab tab)
    {
        if (tab.Pin is not null || tab.IsBlank || tab.IsOwn || tab.Private) return;
        tab.Pin = tab.Monogram is { Length: > 0 } letter ? letter : "•";
        tab.Home = tab.Url;
        Move(tab, All.Count(other => other.Pin is not null && other != tab));
    }

    /// Та же страница рядом с этой, перед глазами (duplicate апстрима).
    /// У приватной — приватная. Пустую и свою страницу не дублируют.
    public void Duplicate(Tab tab)
    {
        if (tab.IsBlank || tab.IsOwn || !Uri.TryCreate(tab.Url, UriKind.Absolute, out var page)) return;
        OpenLink(page, tab, front: true);
    }

    /// Все, кроме этой (closeOthers апстрима): закреплённые кладутся, а не
    /// закрываются, — как Close.
    public void CloseOthers(Tab keep)
    {
        Select(keep);
        foreach (var other in All.Where(tab => tab != keep).ToList()) Close(other);
    }

    /// Незакреплённые ниже этой (tabsBelow апстрима).
    public List<Tab> Below(Tab tab) => All.IndexOf(tab) is var at and >= 0 ? [.. All.Skip(at + 1).Where(other => other.Pin is null)] : [];

    /// Закрыть все ниже этой (closeTabsBelow апстрима). Снизу вверх: тогда
    /// Ctrl+Shift+T вернёт их сверху вниз, каждую на её место.
    public void CloseBelow(Tab tab)
    {
        var going = Below(tab);
        // Сначала уйти с закрываемой, иначе соседки просыпались бы, чтобы тут же закрыться.
        if (Live is { } live && going.Contains(live)) Select(tab);
        for (var i = going.Count - 1; i >= 0; i--) Close(going[i]);
    }

    /// Есть что вернуть по Ctrl+Shift+T: пункт меню вкладки погашен, когда нечего.
    public bool CanReopen => closed.Count > 0;

    /// Выключить или вернуть звук вкладки (toggleMute апстрима). Спящую не
    /// будим: звук снимет Wake, когда движок построят снова.
    public void Mute(Tab tab)
    {
        tab.Muted = !tab.Muted;
        if (tab.View?.CoreWebView2 is { } core) core.IsMuted = tab.Muted;
        tab.Notify();
        Save();
    }

    /// Открепить: в начало незакреплённых.
    public void Unpin(Tab tab)
    {
        if (tab.Pin is null) return;
        tab.Pin = tab.Home = null;
        Move(tab, All.Count(other => other.Pin is not null && other != tab));
    }

    /// Перенести вкладку на место place, куда её дотащили в колонке
    /// (move(_:to:) апстрима): закреплённая остаётся среди закреплённых,
    /// остальные — после них.
    public void Place(Tab tab, int place)
    {
        var pins = All.Count(other => other.Pin is not null);
        place = tab.Pin is null ? Math.Clamp(place, pins, All.Count - 1) : Math.Clamp(place, 0, pins - 1);
        var from = All.IndexOf(tab);
        if (from >= 0 && from != place) Move(tab, place);
    }

    void Move(Tab tab, int place)
    {
        All.Remove(tab);
        All.Insert(place, tab);
        tab.Notify();
        Save();
        Changed?.Invoke();
    }

    /// Закреплённую не закрывают, а кладут (Browser.swift, close): место и
    /// буква остаются, страница отдаётся, а мы возвращаемся, где были.
    // ponytail: кладётся на том адресе, где была, — «домой» (pinsGoHome
    // апстрима) не возвращается. Понадобится — Go(Home) перед Strip.
    void PutDown(Tab tab)
    {
        if (Live == tab)
        {
            var back = previous is { } before && before != tab && All.Contains(before) ? before
                : All.FirstOrDefault(other => other != tab && other.Pin is null) ?? All.FirstOrDefault(other => other != tab);
            if (back is null) Open(null);
            else Select(back);
        }
        Strip(tab);
        tab.Loading = false;
        tab.Notify();
        Save();
        Changed?.Invoke();
    }

    public void Step(int by)
    {
        if (Live is null || All.Count < 2) return;
        Select(All[((All.IndexOf(Live) + by) % All.Count + All.Count) % All.Count]);
    }

    /// Ctrl+1–Ctrl+9 (select(index:) апстрима): вкладка с этим номером сверху,
    /// закреплённые в счёте; девятая — последняя. Такой нет — ничего.
    public void Number(int number)
    {
        var at = number == 9 ? All.Count - 1 : number - 1;
        if (at >= 0 && at < All.Count) Select(All[at]);
    }

    public bool CanGoBack => Live?.View?.CoreWebView2?.CanGoBack == true;
    public bool CanGoForward => Live?.View?.CoreWebView2?.CanGoForward == true;
    public void Back() { if (CanGoBack) Live!.View!.CoreWebView2.GoBack(); }
    public void Forward() { if (CanGoForward) Live!.View!.CoreWebView2.GoForward(); }
    public void Reload() => Live?.View?.CoreWebView2?.Reload();

    /// Перезагрузка мимо кэша (reload(fromOrigin:) апстрима): у Reload движка
    /// такой нет — через отладочный протокол.
    public async void ReloadOrigin()
    {
        if (Live?.View?.CoreWebView2 is not { } core) return;
        try { await core.CallDevToolsProtocolMethodAsync("Page.reload", """{"ignoreCache":true}"""); }
        // Вкладку закрыли, пока звали.
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ObjectDisposedException) { }
    }

    // Печать и DevTools движок открывает сам по своим клавишам, но только пока
    // фокус внутри страницы. Из адресной строки и колонки вкладок эти клавиши
    // до движка не доходят, поэтому оболочка берёт их на себя.
    // Browser: диалог печати самого браузера, как у Ctrl+P в движке (второй вид — системный).
    public void Print() => Live?.View?.CoreWebView2?.ShowPrintUI(Microsoft.Web.WebView2.Core.CoreWebView2PrintDialogKind.Browser);
    public void Inspect() => Live?.View?.CoreWebView2?.OpenDevToolsWindow();

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
        // Куда ложатся файлы (downloads апстрима, Settings › General). Без
        // настройки — папка движка (Загрузки Windows), а в тестовом мире — его
        // папка, не Загрузки человека.
        if (Settings.Text("downloads") is { Length: > 0 } folder) profile.DefaultDownloadFolderPath = folder;
        else if (Store.Testing) profile.DefaultDownloadFolderPath = Path.Combine(Store.Folder, "Downloads");
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
        // Пустая вкладка — не место, куда возвращаются; приватная не оставляет следа.
        var kept = All.Where(tab => !tab.IsBlank && !tab.Private).ToList();
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
            tab.Sheet = tab.Url == Own.History.AbsoluteUri ? new HistoryPage(this)
                : tab.Url == Own.Downloads.AbsoluteUri ? new DownloadsPage()
                : tab.Url == Own.Bookmarks.AbsoluteUri ? new BookmarksPage(this) : new SettingsPage(this);
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
            var environment = await Shared();
            if (tab.Private)
            {
                // Свой профиль в памяти на каждую приватную вкладку (и на те,
                // что открыты из неё): у двух приватных куки и входы разные,
                // как в апстриме.
                var options = environment.CreateCoreWebView2ControllerOptions();
                options.IsInPrivateModeEnabled = true;
                options.ProfileName = tab.Shy;
                await view.EnsureCoreWebView2Async(environment, options);
            }
            else await view.EnsureCoreWebView2Async(environment);
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
        // Оборванный первый переход движка повторяем один раз: в свежем профиле
        // с только что поставленным uBlock Chromium обрывает первый заход на
        // Chrome Web Store (ConnectionAborted), повтор проходит (задача 19).
        // Так же обрывается переход, ставший загрузкой, а DownloadStarting
        // приходит уже после конца перехода: повтор той загрузки отменяем там.
        // ponytail: файл, открытый в новой вкладке, запрашивается дважды (второй
        // раз — до заголовков). Мешает — отличать срыв магазина от загрузки иначе.
        bool retried = false;
        int echoes = 0;
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
            tab.Failure = done.IsSuccess ? null : $"{done.WebErrorStatus}, {core.Source}";
            tab.Loading = false;
            if (done.IsSuccess)
            {
                // Заголовок берём и здесь: у новой страницы с тем же
                // заголовком, что у прежней, событие о его смене не приходит.
                tab.Title = Titled(core);
                if (!tab.Private && Uri.TryCreate(core.Source, UriKind.Absolute, out var page)) History.Record(page, tab.Title);
                Save();
            }
            else if (done.NavigationId == going && !retried && core.Source == "about:blank"
                && done.WebErrorStatus == Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.ConnectionAborted)
            {
                retried = true;
                core.Navigate(tab.Url);
                return;
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
            if (tab.Landed && !tab.Private && Uri.TryCreate(core.Source, UriKind.Absolute, out var page)) History.Retitle(page, tab.Title);
            tab.Notify();
            Save();
            if (tab == Live) Changed?.Invoke();
        };
        // Назад и вперёд зажигаются и гаснут вместе с историей страницы.
        core.HistoryChanged += (_, _) => { if (tab == Live) Changed?.Invoke(); };
        // Видео во весь экран: окно следует за живой вкладкой (MainWindow.Immerse).
        core.ContainsFullScreenElementChanged += (_, _) => { if (tab == Live) Changed?.Invoke(); };
        // Звук: заглушённой вкладке он снимается до первой загрузки — у спавшей
        // движок строится заново. Динамик в строке — пока страница играет.
        core.IsMuted = tab.Muted;
        core.IsDocumentPlayingAudioChanged += (_, _) =>
        {
            // Движок, оставленный докачивать файлы (Strip), о вкладке не говорит.
            if (tab.View != view) return;
            tab.Noisy = core.IsDocumentPlayingAudio;
            tab.Notify();
        };
        // Значок, который объявила страница, движок уже скачал: кладём его в
        // запас, и знаки сайта во вкладках, истории и подсказках надевают его.
        core.FaviconChanged += async (_, _) =>
        {
            // Приватная значков на диск не кладёт.
            if (tab.Private || !Uri.TryCreate(core.Source, UriKind.Absolute, out var page) || Icons.Site(page) is not { } site
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
            if (!asked.IsUserInitiated || !Uri.TryCreate(asked.Uri, UriKind.Absolute, out var wanted) || Own.Is(wanted)) return;
            // Средняя кнопка или Ctrl — за спиной, с Shift — перед глазами;
            // обычный щелчок (target="_blank") — перед глазами. Как давно было
            // нажатие, смотрим, чтобы старое не решило за новое окно.
            // ponytail: нажатие держат дольше двух секунд — вкладка встанет перед глазами.
            var behind = tab.Click is { Front: false } click && DateTime.UtcNow - click.At < TimeSpan.FromSeconds(2);
            tab.Click = null;
            OpenLink(wanted, tab, front: !behind);
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
            else if (PageKeys.Clicked(text, KeyToken) is { } front) tab.Click = (DateTime.UtcNow, front);
        }
        // Ctrl+W в странице движок делает сам (раз мы принимаем NewWindowRequested)
        // и просит закрыть окно; так же — window.close() страницы, которой Chrome
        // разрешил бы закрыться: открытой самой страницей или с одной записью в
        // истории. Окно у нас — вкладка.
        core.WindowCloseRequested += (_, _) => { if (tab.View == view) Close(tab); };
        // Загрузка живёт в движке страницы: пока она идёт или стоит на паузе,
        // вкладка не спит. Окошко движка не показываем: список — своя страница.
        core.DownloadStarting += async (_, start) =>
        {
            start.Handled = true;
            var download = start.DownloadOperation;
            // Вторая загрузка после повтора, пока у движка нет страницы, — та же
            // самая, начатая повтором. Не по адресу: у ссылок на файл с подписью
            // он от запроса к запросу разный.
            if (retried && core.Source == "about:blank" && ++echoes == 2) { start.Cancel = true; return; }
            // Спросить, куда положить (downloads.ask апстрима, Settings › General):
            // загрузка ждёт ответа под отсрочкой; передумали — её нет.
            if (Settings.Bool("downloads.ask"))
            {
                var wait = start.GetDeferral();
                try
                {
                    var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(stage.XamlRoot.ContentIslandEnvironment.AppWindowId)
                    {
                        SuggestedFileName = Path.GetFileName(start.ResultFilePath),
                        SuggestedFolder = Path.GetDirectoryName(start.ResultFilePath) ?? "",
                    };
                    if (await picker.PickSaveFileAsync() is { } chosen) start.ResultFilePath = chosen.Path;
                    else start.Cancel = true;
                }
                // Окно выбора не открылось: файл ляжет, куда лёг бы без вопроса.
                catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
                finally { wait.Complete(); }
                if (start.Cancel || tab.View != view) return;
            }
            tab.Downloads.Add(download);
            Fetching.Watch(download, tab);
            download.StateChanged += (_, _) =>
            {
                if (download.State == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Completed
                    || (download.State == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Interrupted && !download.CanResume))
                    tab.Downloads.Remove(download);
            };
        };
        // Ctrl+J в странице движок ловит сам и открывает своё окошко: вместо
        // него — своя страница.
        core.IsDefaultDownloadDialogOpenChanged += (_, _) =>
        {
            if (!core.IsDefaultDownloadDialogOpen) return;
            core.CloseDefaultDownloadDialog();
            Show(Own.Downloads);
        };
        core.WebMessageReceived += (_, said) => Heard(said);
        core.FrameCreated += (_, made) => made.Frame.WebMessageReceived += (_, said) => Heard(said);
        await Rekey(tab, core);
        if (tab.View != view) return;
        // uBlock живёт в общем профиле; в приватной его нет — там работает свой список.
        if (!tab.Private) await (ublock ??= UBlock.Ensure(core.Profile));
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
        if (Engine() is { } live)
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
