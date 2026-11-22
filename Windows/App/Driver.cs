using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Browser;

// Команды тест-канала: первые из тех, что есть в Sources/Search/Bench.swift.
// Так окно проверяется без мыши и клавиатуры человека. Имена команд свои там,
// где в апстриме нет прямого соответствия.
sealed partial class MainWindow
{
    /// Буфер обмена для Paste and Go в тест-канале: настоящий — человека.
    string clip = "";

    /// Запрос пришёл в фоновом потоке; вкладки живут в потоке интерфейса.
    Task<JsonNode?> Answer(JsonElement request)
    {
        var done = new TaskCompletionSource<JsonNode?>();
        var queued = DispatcherQueue.TryEnqueue(async () =>
        {
            try { done.SetResult(await Run(request)); }
            catch (Exception e) { done.SetException(e); }
        });
        if (!queued) done.SetException(new InvalidOperationException("the window is closing"));
        return done.Task;
    }

    /// Видимые потомки элемента: спрятанное и всё, что внутри него, пропускается.
    static IEnumerable<DependencyObject> Within(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Visibility: Visibility.Collapsed }) continue;
            yield return child;
            foreach (var deeper in Within(child)) yield return deeper;
        }
    }

    /// Имя элемента, как его прочтёт экранный диктор: заданное нами, а у
    /// строки меню — её текст.
    static string Called(Control control) =>
        AutomationProperties.GetName(control) is { Length: > 0 } given ? given : (control as MenuFlyoutItem)?.Text ?? "";

    async Task<JsonNode?> Run(JsonElement request)
    {
        string Text(string name) => request.GetProperty(name).GetString() ?? "";
        bool Flag(string name) => request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        Tab Named() => tabs.All.FirstOrDefault(tab => tab.Id.ToString() == Text("id"))
            ?? throw new ArgumentException("no such tab");
        // Элементы своей страницы, которая сейчас перед глазами, и меню,
        // открытого над ней: оно живёт в отдельном всплывающем окне.
        IEnumerable<Control> Controls()
        {
            var sheet = tabs.Live?.Sheet ?? throw new ArgumentException("the live tab is not a page of the browser's own");
            return VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Select(popup => (DependencyObject)popup.Child)
                .Prepend(sheet).SelectMany(Within).OfType<Control>().Where(control => Called(control).Length > 0);
        }
        // Полоса и колонка видны на любой странице, в том числе на чужой.
        // Адресная строка — сама кнопка, а не то, что внутри неё; спрятанная (режим
        // фокуса) в список не входит, как и спрятанная колонка. В конце — открытые
        // окошки полосы (карточка сайта).
        IEnumerable<Control> Chrome() => (bar.View.Visibility == Visibility.Visible ? new Control[] { bar.Site, bar.Edit } : [])
            .Concat(new DependencyObject[] { doors, panel, actions.View, offer.View, find.View }
                .Concat(VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Select(popup => popup.Child).OfType<DependencyObject>())
                .Where(part => part is not UIElement { Visibility: Visibility.Collapsed })
                .SelectMany(Within).OfType<Control>())
            .Where(control => control.Visibility == Visibility.Visible && Called(control).Length > 0);
        // То, что прочтёт экранный диктор, — одно описание и для sheet, и для chrome.
        JsonNode Describe(Control control) => new JsonObject
        {
            ["name"] = Called(control), ["kind"] = control.GetType().Name,
            ["on"] = (control as ToggleButton)?.IsChecked, ["enabled"] = control.IsEnabled,
            ["text"] = control is TextBox box ? box.Text : string.Join(" ", Within(control).OfType<TextBlock>().Select(word => word.Text)),
        };
        // Своя страница первой: её имена могут совпасть с именами полосы.
        Control Control(string name) =>
            (tabs.Live?.Sheet is null ? null : Controls().FirstOrDefault(control => Called(control) == name))
            ?? Chrome().FirstOrDefault(control => Called(control) == name)
            ?? throw new ArgumentException("no such control");

        switch (request.GetProperty("do").GetString())
        {
            case "tabs":
                return new JsonArray(tabs.All.Select(tab => (JsonNode)new JsonObject
                {
                    ["id"] = tab.Id.ToString(), ["url"] = tab.Url, ["title"] = tab.Title,
                    ["live"] = tab == tabs.Live, ["asleep"] = tab.View is null, ["loading"] = tab.Loading, ["pin"] = tab.Pin, ["private"] = tab.Private, ["failure"] = tab.Failure,
                    ["noisy"] = tab.Noisy, ["muted"] = tab.Muted, ["engineMuted"] = tab.View?.CoreWebView2?.IsMuted,
                }).ToArray());
            case "open":
                return new JsonObject { ["id"] = tabs.Open(new Uri(Text("url")), select: !Flag("behind")).Id.ToString() };
            case "new":
                // Как Ctrl+T: пустая вкладка с полем.
                return new JsonObject { ["id"] = tabs.Open(null).Id.ToString() };
            case "private":
                // Как Ctrl+Shift+N: пустая приватная вкладка с полем.
                return new JsonObject { ["id"] = tabs.OpenPrivate().Id.ToString() };
            case "go":
                // Как набранное в поле и Enter.
                if (Tabs.Destination(Text("text")) is not { } destination) throw new ArgumentException("goes nowhere");
                field.Close();
                tabs.Go(destination);
                return new JsonObject { ["url"] = destination.AbsoluteUri, ["id"] = tabs.Live?.Id.ToString() };
            case "command":
                // Команда окна по имени (Shortcuts), как её сочетание вне страницы:
                // ускоритель окна тест-канал не нажмёт.
                if (!acts.TryGetValue(Text("id"), out var act)) throw new ArgumentException("no such command");
                act();
                return new JsonObject();
            case "clip":
                // Что Paste and Go возьмёт вместо буфера обмена.
                clip = Text("text");
                return new JsonObject { ["clip"] = clip };
            case "select":
                tabs.Select(Named());
                return new JsonObject();
            case "close":
                tabs.Close(Named());
                return new JsonObject();
            case "carry":
            {
                // Как строка, которую протащили указателем на travel точек и
                // отпустили (сам жест — только мышью человека); без done — ещё несут.
                Carry(Named(), request.GetProperty("travel").GetDouble(), Flag("done"));
                return new JsonObject();
            }
            case "wait":
            {
                // Пока вкладка не догрузится: вместо пауз наугад. Догрузилась —
                // значит не грузится уже полсекунды подряд: отменённый прежний
                // переход на миг гасит загрузку раньше, чем начнётся новый.
                var tab = Named();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                long? calm = null;
                while (watch.ElapsedMilliseconds < 30_000)
                {
                    await Task.Delay(50);
                    if (tab.Loading || tab.View?.CoreWebView2 is null) calm = null;
                    else if ((calm ??= watch.ElapsedMilliseconds) + 500 <= watch.ElapsedMilliseconds) break;
                }
                return new JsonObject
                {
                    ["loading"] = tab.Loading, ["url"] = tab.Url, ["title"] = tab.Title,
                    // Когда загрузка кончилась, без полсекунды проверки тишины.
                    ["ms"] = calm ?? watch.ElapsedMilliseconds,
                };
            }
            case "js":
                // Выполнить на странице и вернуть результат: то, что видно
                // только изнутри страницы, иначе не проверить.
                if (Named().View?.CoreWebView2 is not { } page) throw new ArgumentException("the tab is asleep");
                return JsonNode.Parse(await page.ExecuteScriptAsync(Text("code")));
            case "cdp":
                // Команда отладочного протокола движка: колесо и указатель
                // попадают в страницу как настоящие, а мышь человека не тронута.
            {
                if (Named().View?.CoreWebView2 is not { } engine) throw new ArgumentException("the tab is asleep");
                var call = engine.CallDevToolsProtocolMethodAsync(Text("method"),
                    request.TryGetProperty("params", out var given) ? given.GetRawText() : "{}").AsTask();
                // Нажатие может закрыть саму вкладку (Ctrl+W): закрытый движок
                // не отвечает никогда, и канал висел бы вместе с проверкой.
                if (await Task.WhenAny(call, Task.Delay(5000)) != call) return new JsonObject { ["unanswered"] = true };
                return JsonNode.Parse(await call);
            }
            case "field":
                field.Open(request.TryGetProperty("text", out var prefill) ? prefill.GetString() ?? "" : "");
                return new JsonObject();
            case "type":
                field.Type(Text("text"));
                return new JsonObject();
            case "theme":
                // Тем же путём, что и выбор на странице настроек.
                Settings.Set("look", Text("look"));
                return new JsonObject();
            case "settings":
                // Как Ctrl+запятая.
                tabs.Show(Own.Settings);
                return new JsonObject { ["id"] = tabs.Live?.Id.ToString() };
            case "history":
                // Как Ctrl+H.
                tabs.Show(Own.History);
                return new JsonObject { ["id"] = tabs.Live?.Id.ToString() };
            case "visit":
                // Место в истории, как если бы там побывали тогда-то: страницу
                // истории не проверить на одном сегодняшнем дне.
                tabs.History.Take(new Uri(Text("url")), Text("title"), 1, DateTime.Now.AddHours(-request.GetProperty("ago").GetDouble()));
                return new JsonObject();
            case "focus":
                // Фокус с клавиатуры на элемент своей страницы.
                return new JsonObject { ["focused"] = Control(Text("name")).Focus(FocusState.Keyboard) };
            case "step":
            {
                // Куда фокус уходит с клавиатуры: next и previous — Tab и
                // Shift+Tab, up, down, left, right — стрелки.
                var moved = FocusManager.TryMoveFocus(Enum.Parse<FocusNavigationDirection>(Text("way"), ignoreCase: true),
                    new FindNextElementOptions { SearchRoot = root });
                var on = FocusManager.GetFocusedElement(root.XamlRoot) as Control;
                return new JsonObject { ["moved"] = moved, ["on"] = on is null ? null : Called(on), ["kind"] = on?.GetType().Name };
            }
            case "set":
            {
                // Записать настройку, как её пишет элемент страницы настроек.
                var value = request.GetProperty("value");
                if (value.ValueKind == JsonValueKind.String) Settings.Set(Text("key"), value.GetString() ?? "");
                else if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) Settings.Set(Text("key"), value.GetBoolean());
                else if (value.ValueKind == JsonValueKind.Number) Settings.Set(Text("key"), value.GetDouble());
                else Settings.Remove(Text("key"));
                return new JsonObject();
            }
            case "sheet":
                // Что на своей странице: элементы с именами, как их видит
                // экранный диктор. Спрятанные не считаются.
                return new JsonArray(Controls().Select(Describe).ToArray());
            case "chrome":
                // Полоса окна и колонка вкладок: то же описание, что у sheet.
                return new JsonArray(Chrome().Select(Describe).ToArray());
            case "press":
            {
                // Нажать элемент своей страницы тем же путём, что и экранный
                // диктор: заодно проверяется, что элемент ему виден.
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Control(Text("name")));
                if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider option) option.Select();
                else if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
                else if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider button) button.Invoke();
                else throw new ArgumentException("nothing to press");
                return new JsonObject();
            }
            case "fill":
                // Как набранное в поле своей страницы.
                ((TextBox)Control(Text("name"))).Text = Text("text");
                return new JsonObject();
            case "choose":
                // Выбор мест на странице истории: Ctrl+щелчок по строке name
                // (shift — Shift+щелчок), all — Ctrl+A, none — Esc.
                if (tabs.Live?.Sheet is not HistoryPage list) throw new ArgumentException("history is not shown");
                return new JsonObject { ["chosen"] = list.Choose(request.TryGetProperty("name", out var name) ? name.GetString() : null, Flag("shift"), Flag("all"), Flag("none")) };
            case "point":
            {
                // Как указатель мыши, вставший на строку истории name и оставшийся там.
                if (tabs.Live?.Sheet is not HistoryPage history) throw new ArgumentException("history is not shown");
                history.Point(Text("name"));
                return new JsonObject();
            }
            case "menu":
            {
                // Как правый щелчок по названному элементу: его меню.
                var control = Control(Text("name"));
                (control.ContextFlyout ?? throw new ArgumentException("no menu")).ShowAt(control);
                return new JsonObject();
            }
            case "dismiss":
                // Как нажатие мимо открытого меню: проверка не оставляет его
                // висеть поверх окон человека.
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)) popup.IsOpen = false;
                return new JsonObject();
            case "fold":
                // Как Ctrl+S, но к названному состоянию.
                if (folded != Flag("on")) ToggleFold();
                return new JsonObject { ["folded"] = folded };
            case "hover":
                // Как указатель над адресной строкой и его уход.
                bar.Hover(Flag("on"));
                return new JsonObject();
            case "reveal":
                // Как указатель у левого края и как его уход с колонки.
                if (Flag("on")) Reveal();
                else Conceal();
                return new JsonObject { ["revealed"] = revealed };
            case "crown":
                // Как указатель у верхнего края в режиме фокуса, который там и
                // остаётся (указатель человека где-то ещё), и как его уход.
                if (Flag("on"))
                {
                    Crown();
                    watching.Stop();
                }
                else Uncrown();
                return new JsonObject { ["crowned"] = crowned };
            case "actions":
                // Кнопки расширений в полосе, по порядку.
                return new JsonArray(actions.All.Select(button => (JsonNode)new JsonObject { ["id"] = button.Id, ["name"] = button.Name }).ToArray());
            case "act":
                // Как нажатие на кнопку расширения: его окно открывается (открытое — закрывается).
                actions.Press(Text("id"));
                return new JsonObject();
            case "popup":
            {
                // Открытое окно расширения: адрес, размер и, с code, ответ
                // выражения в нём (обещание дожидается).
                if (actions.Popup is not { } popup) return new JsonObject { ["open"] = false };
                JsonNode? said = null;
                if (request.TryGetProperty("code", out var code) && popup.Core is { } inside)
                {
                    var answer = JsonNode.Parse(await inside.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                        JsonSerializer.Serialize(new { expression = code.GetString(), awaitPromise = true, returnByValue = true })));
                    said = answer?["result"]?["value"]?.DeepClone() ?? answer?["exceptionDetails"]?["exception"]?["description"]?.DeepClone();
                }
                return new JsonObject
                {
                    ["open"] = true, ["url"] = popup.Core?.Source, ["width"] = popup.Width, ["height"] = popup.Height, ["said"] = said,
                };
            }
            case "shield":
                // Открытые страницы узнают об этом из Settings.Changed.
                Shield.Enabled = Flag("on");
                return new JsonObject { ["on"] = Shield.Enabled };
            case "print":
                // Как Ctrl+P.
                tabs.Print();
                return new JsonObject();
            case "downloads":
            {
                // Как Ctrl+J; в ответе — идущие загрузки и список скачанного.
                tabs.Show(Own.Downloads);
                var active = new JsonArray();
                foreach (var entry in Fetching.Active)
                    active.Add(new JsonObject
                    {
                        ["name"] = entry.Name, ["from"] = entry.From, ["progress"] = entry.Progress,
                        ["paused"] = entry.Paused, ["failed"] = entry.Failed,
                    });
                var kept = new JsonArray();
                foreach (var keep in Downloads.Kept)
                    kept.Add(new JsonObject { ["name"] = keep.Name, ["from"] = keep.From, ["path"] = keep.Path, ["address"] = keep.Address });
                return new JsonObject { ["id"] = tabs.Live?.Id.ToString(), ["active"] = active, ["kept"] = kept };
            }
            case "bookmarks":
            {
                // Страница закладок; в ответе — все сайты по порядку, с папками.
                tabs.Show(Own.Bookmarks);
                var sites = new JsonArray();
                foreach (var (node, path) in Bookmarks.Sites())
                    sites.Add(new JsonObject { ["title"] = node.Title, ["url"] = node.Url, ["path"] = string.Join("/", path) });
                return new JsonObject { ["id"] = tabs.Live?.Id.ToString(), ["sites"] = sites };
            }
            case "extension":
            {
                // Поставить распакованное расширение из папки тем же путём, что
                // «Choose…» в настройках: копия в хранилище, её — движку.
                (string Id, string Name) added = default;
                await tabs.WithProfile(async profile => added = await Addons.Install(profile, Text("path")));
                return new JsonObject { ["id"] = added.Id, ["name"] = added.Name };
            }
            case "extensions":
            {
                if (tabs.Profile is not { } profile) throw new ArgumentException("no engine is awake");
                return new JsonArray((await profile.GetBrowserExtensionsAsync()).Select(found => (JsonNode)new JsonObject
                {
                    ["id"] = found.Id, ["name"] = found.Name, ["enabled"] = found.IsEnabled,
                }).ToArray());
            }
            case "keys":
                // Сочетания, которые окно зарегистрировало. Ctrl в тихое окно не
                // послать, так что проверка смотрит здесь.
                return new JsonArray(root.KeyboardAccelerators.Select(shortcut => (JsonNode)(
                    shortcut.Modifiers == VirtualKeyModifiers.None ? $"{shortcut.Key}" : $"{shortcut.Modifiers}+{shortcut.Key}")).ToArray());
            case "probe":
                return new JsonObject
                {
                    ["tabs"] = tabs.All.Count, ["live"] = tabs.Live?.Id.ToString(), ["field"] = field.IsOpen,
                    ["dark"] = root.ActualTheme == ElementTheme.Dark,
                    ["back"] = tabs.CanGoBack, ["forward"] = tabs.CanGoForward,
                    ["shield"] = Shield.Enabled, ["blocked"] = Blocking.Blocked,
                    // По какому шаблону сейчас ушли бы слова из поля.
                    ["engine"] = Engines.Current,
                    ["folded"] = folded, ["revealed"] = revealed,
                    // Ширина страницы: сложенная колонка должна её отдавать.
                    ["card"] = Math.Round(card.ActualWidth),
                    // Места полосы, где нажатие своё, а не перетаскивание окна:
                    // x, y, ширина, высота в точках экрана от угла окна.
                    ["holes"] = new JsonArray([.. (InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                        .GetRegionRects(NonClientRegionKind.Passthrough) ?? []).Select(hole => (JsonNode)new JsonArray(hole.X, hole.Y, hole.Width, hole.Height))]),
                    ["width"] = AppWindow.ClientSize.Width,
                    // Где окно таскают, и видны ли кнопки окна.
                    ["caption"] = new JsonArray([.. (InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                        .GetRegionRects(NonClientRegionKind.Caption) ?? []).Select(hole => (JsonNode)new JsonArray(hole.X, hole.Y, hole.Width, hole.Height))]),
                    ["buttons"] = AppWindow.TitleBar.PreferredHeightOption != Microsoft.UI.Windowing.TitleBarHeightOption.Collapsed,
                    ["focused"] = focused, ["crowned"] = crowned, ["immersed"] = immersed, ["find"] = find.Said,
                    // Где фокус с клавиатуры: имя элемента, его вид и как фокус туда попал.
                    ["at"] = FocusManager.GetFocusedElement(root.XamlRoot) is Control at ? $"{Called(at)} | {at.GetType().Name} | {at.FocusState}" : null,
                    // Номер процесса: bench.py stop закрывает по нему окно штатно.
                    ["pid"] = Environment.ProcessId,
                    // Настройки движка, как их видит профиль первого живого движка.
                    ["profile"] = tabs.All.Select(tab => tab.View?.CoreWebView2).FirstOrDefault(core => core is not null)?.Profile is { } own
                        ? new JsonObject
                        {
                            ["tracking"] = own.PreferredTrackingPreventionLevel.ToString(),
                            ["passwords"] = own.IsPasswordAutosaveEnabled, ["autofill"] = own.IsGeneralAutofillEnabled,
                        }
                        : null,
                };
            default:
                throw new ArgumentException("unknown command");
        }
    }
}
