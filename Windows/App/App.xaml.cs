using Microsoft.UI.Xaml;

namespace Browser;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Установщик запускает программу со служебными ключами (поставили, обновили,
        // удаляют): Velopack должен ответить и выйти раньше, чем появится окно.
        // При обычном запуске эта строка ничего не делает.
        Velopack.VelopackApp.Build().Run();
        // Дальше — то же, что XamlGeneratedMain, но между ними второй запуск
        // отдаёт свои адреса уже открытому окну и выходит (Links).
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (Links.Elsewhere()) return;
        Application.Start(_ =>
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(queue));
            new App();
        });
    }
}

public partial class App : Application
{
    MainWindow? window;

    /// Прогон под управлением скрипта (BROWSER_QUIET) рядом с человеком за этим компьютером.
    internal static readonly bool Quiet = Environment.GetEnvironmentVariable("BROWSER_QUIET") is not null;

    /// Закрыть окно как обычно — вкладки и история сохраняются — и тем выйти
    /// (обновление изнутри: установщик ждёт выхода).
    public static void Quit() => (Current as App)?.window?.Close();

    public App()
    {
        // Куки, входы и кэш движка лежат рядом с остальным, в папке хранилища:
        // у тестового мира они свои, и «выйти отовсюду» в тесте не разлогинит
        // настоящий браузер. Задать надо до первого WebView2.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Store.File("WebView2"));
        // Полоса прокрутки страницы: тонкий ползунок поверх содержимого, который
        // появляется при прокрутке и не отнимает у страницы ширину, как на
        // macOS. Без флага движок рисует широкую полосу с дорожкой и стрелками
        // на 15 точек. Похожий по имени флаг FluentOverlayScrollbar на движке
        // 157 ничего не меняет: проверено замером ширины страницы.
        const string arguments = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";
        Environment.SetEnvironmentVariable(arguments,
            $"{Environment.GetEnvironmentVariable(arguments)} --enable-features=OverlayScrollbar".Trim());
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        // Прогон под управлением скрипта (BROWSER_QUIET) идёт рядом с человеком,
        // который работает за этим же компьютером: окно не забирает клавиатуру
        // и не встаёт поверх его окон. Иначе его Ctrl+S и Ctrl+Tab попадают в
        // проверяемый браузер — так уже было.
        if (Quiet)
        {
            window.AppWindow.Show(activateWindow: false);
            window.AppWindow.MoveInZOrderAtBottom();
        }
        else
        {
            // Закрыли развёрнутым — развёрнутым и открывается. Тихому окну не
            // разворачиваться: развёртывание делает окно активным.
            if (Settings.Bool("window.zoomed") && window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Maximize();
            window.Activate();
        }
    }
}
