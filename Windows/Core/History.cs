using System.Text;
using System.Text.Json;

namespace Browser;

// Перенос Sources/Search/History.swift.
//
// Где вы были, чтобы поле могло дописать адрес за вас. Один небольшой файл
// рядом с остальным, который пишется чуть позже визита, а не на каждое
// нажатие.
//
// Что не перенесено и почему:
// - фоновая загрузка файла: здесь она синхронная (см. Load);
// - перенос «кредитов» из файлов апстрима до 1.0.4: таких файлов у новой
//   программы нет;
// - список последних мест для меню History: на Windows нет строки меню.
//
// Добавлено: Lines — раскладка по дням для страницы истории. В апстриме её
// считает сама панель (HistoryPanel.refresh в Recall.swift).
//
// Отличие от macOS: System.Uri нормализует адрес, поэтому порт по умолчанию и
// лишнее процентное кодирование не дают отдельной записи. Регистр пути и
// запроса по-прежнему различается.

/// Одна строка под полем.
public sealed record Suggestion(string Key, string Title, Uri Url, Suggestion.Kinds Kind)
{
    public enum Kinds
    {
        /// Место, где вы действительно были.
        Visited,
        /// Известный адрес, который поле знает с первого дня.
        Known,
    }

    /// По странице: два адреса, отличающиеся только регистром, — две строки.
    public string Id => Kind == Kinds.Visited ? History.Identity(Url) : Key;
}

internal sealed class Visit
{
    public required string Url { get; set; }
    public required string Key { get; set; }
    public string Title { get; set; } = "";
    /// long, а не int: в файле с Mac счётчик 64-битный, и правленый руками
    /// файл с огромным числом не должен отправлять всю историю в карантин.
    public long Count { get; set; }
    public DateTime Last { get; set; }

    public Visit Copy() => (Visit)MemberwiseClone();
}

public sealed class History
{
    /// Всё, где вы были, для окна истории.
    public sealed record Trace(string Key, string Address, string Title, Uri Url, DateTime Last, long Count);

    /// Больше визитов не считаем: столько никто не сделает, а сложение не переполнится.
    public const long MostVisits = 1_000_000_000;
    /// Сколько мест хранится: в файле и в памяти после обрезки (см. Prune).
    public const int Cap = 2_000;
    /// Насколько память может перерасти Cap до обрезки, чтобы насыщенный день
    /// сортировал историю раз в несколько сотен страниц, а не на каждой. И
    /// столько же самых новых мест обрезка оставляет при любом счёте.
    public const int Slack = 250;

    /// Пауза перед записью: насыщенная минута пишет файл один раз, а не тридцать.
    internal static TimeSpan SavePause = TimeSpan.FromSeconds(1.5);

    /// «Месяц» частоты-и-давности, в секундах.
    const double Fading = 30.0 * 86_400;
    /// Имя, которое программа просто знает: меньше одного свежего визита.
    static readonly double KnownWeight = Math.Log(1.5);

    // Выводится один раз, когда визит попадает в память. Подсказки считаются
    // на каждое нажатие: ни разбору адресов, ни кодированию в UTF-8 в том
    // цикле не место.
    sealed class PageText(string address, string searchable, string host, bool homepage, byte[] findable, string[] trail)
    {
        public readonly string Address = address;
        public readonly string Searchable = searchable;
        public readonly byte[] SearchableBytes = Encoding.UTF8.GetBytes(searchable);
        public readonly string Host = host;
        public readonly bool Homepage = homepage;
        public readonly byte[] Findable = findable;
        /// Префиксы, которым засчитываются визиты: host, host/team, host/team/api.
        public readonly string[] Trail = trail;
        public double Weight;
    }

    // Один замок на всё: запись по таймеру приходит из другого потока, а
    // подсказки потом можно будет считать не в потоке интерфейса.
    readonly Lock gate = new();
    readonly Dictionary<string, Visit> visits = [];
    readonly Dictionary<string, PageText> pageText = [];
    /// Вес под каждым префиксом адреса на момент epoch; затухает при чтении.
    readonly Dictionary<string, double> weights = [];
    HashSet<string> visitedHosts = [];
    readonly DateTime epoch = DateTime.UtcNow;
    readonly string file;
    /// В памяти есть то, чего ещё нет в файле.
    bool owed;
    /// Запись ждёт своей паузы.
    bool pausing;
    Timer? timer;

    public History(string name = "history.json")
    {
        file = Store.File(name);
        Load();
    }

    /// Какая это страница. Без фрагмента, имени и пароля; схема и хост в нижнем
    /// регистре; написание пути и запроса сохраняется.
    public static string Identity(Uri url) =>
        url.IsAbsoluteUri && url.Host.Length > 0
            ? url.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)
            : url.OriginalString;

    /// Страница, как она хранится: без имени и пароля в адресе. Они никогда не
    /// были частью того, какая это страница, а файл истории не место для пароля.
    public static Uri Kept(Uri url) =>
        url.UserInfo.Length == 0
            ? url
            : new Uri(url.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped) + url.Fragment);

    static bool IsWeb(Uri url) => url.IsAbsoluteUri && url.Scheme is "http" or "https";

    static Uri Homepage(Uri url) => new(url.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + "/");

    static PageText PageTextFor(Uri url, string title = "")
    {
        var shown = url.Fragment.Length == 0
            ? url
            : new Uri(url.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped));
        var address = Address.Editable(shown);
        var searchable = Stripped(address);
        return new PageText(address, searchable, Address.WithoutWWW(shown.Host), shown.AbsolutePath is "" or "/",
            Encoding.UTF8.GetBytes(title.ToLowerInvariant() + " " + searchable), Trail(searchable));
    }

    static string[] Trail(string searchable)
    {
        var own = searchable.EndsWith('/') ? searchable[..^1] : searchable;
        var cut = own.AsSpan().IndexOfAny('?', '#');
        var path = cut < 0 ? own : own[..cut];
        List<string> trail = [];
        for (var i = 0; i < path.Length; i++)
            if (path[i] == '/') trail.Add(path[..i]);
        if (path.Length < own.Length) trail.Add(path);
        trail.Add(own);
        return [.. trail];
    }

    public static string Stripped(string address)
    {
        var text = address.Trim().ToLowerInvariant();
        foreach (var scheme in (ReadOnlySpan<string>)["https://", "http://"])
            if (text.StartsWith(scheme, StringComparison.Ordinal)) text = text[scheme.Length..];
        return text.StartsWith("www.", StringComparison.Ordinal) ? text[4..] : text;
    }

    void Remember(Visit visit, Uri? url = null)
    {
        if (url is null && !Uri.TryCreate(visit.Url, UriKind.Absolute, out url)) return;
        var text = PageTextFor(url, visit.Title);
        text.Weight = visit.Count * Math.Exp((visit.Last - epoch).TotalSeconds / Fading);
        if (pageText.TryGetValue(visit.Key, out var old)) Credit(old, -old.Weight);
        Credit(text, text.Weight);
        pageText[visit.Key] = text;
        if (text.Host.Length > 0) visitedHosts.Add(text.Host);
    }

    void Credit(PageText text, double weight)
    {
        foreach (var prefix in text.Trail)
        {
            var had = weights.GetValueOrDefault(prefix);
            var now = had + weight;
            // Когда долю последней страницы забрали обратно, остаётся ноль или
            // крошка округления: префикс больше ничей. Если его оставить, ключ
            // здесь получит каждый когда-либо посещённый адрес.
            if (now > had * 1e-9) weights[prefix] = now;
            else weights.Remove(prefix);
        }
    }

    void ForgetText(string key)
    {
        if (!pageText.Remove(key, out var old)) return;
        Credit(old, -old.Weight);
        if (old.Host.Length > 0 && !pageText.Values.Any(text => text.Host == old.Host)) visitedHosts.Remove(old.Host);
    }

    // MARK: запись

    public void Record(Uri url, string title)
    {
        if (!IsWeb(url)) return;
        url = Kept(url);
        var key = Identity(url);
        lock (gate)
        {
            var now = DateTime.UtcNow;
            // Чтение глубокой страницы — это ещё и визит на сайт. Без этого три
            // набранные буквы предлагают статью, открытую на прошлой неделе, а
            // не главную: никто не набирает домен, чтобы попасть в его середину.
            if (url.AbsolutePath is not ("" or "/"))
            {
                var homeUrl = Homepage(url);
                var root = Identity(homeUrl);
                if (!visits.TryGetValue(root, out var home))
                    visits[root] = home = new Visit { Url = homeUrl.AbsoluteUri, Key = root };
                home.Count = Math.Min(MostVisits, home.Count + 1);
                home.Last = now;
                Remember(home, homeUrl);
            }

            if (visits.TryGetValue(key, out var seen))
            {
                seen.Count = Math.Min(MostVisits, seen.Count + 1);
                seen.Last = now;
                seen.Url = url.AbsoluteUri;
                if (title.Length > 0) seen.Title = title;
            }
            else visits[key] = seen = new Visit { Url = url.AbsoluteUri, Key = key, Title = title, Count = 1, Last = now };
            Remember(seen, url);
            Prune();
            Save();
        }
    }

    // Память обрезается до Cap, когда перерастает его на Slack: иначе сессия
    // без перезапуска держит, а поле на каждое нажатие просматривает, все
    // страницы, которые видела. Самые новые Slack мест остаются при любом
    // счёте: страница, открытая минуту назад, весит около 1, меньше любой
    // старой с двумя визитами, но она не должна пропасть из истории и поля,
    // пока вы на ней.
    void Prune()
    {
        if (visits.Count <= Cap + Slack) return;
        var now = DateTime.UtcNow;
        var kept = visits.Values.OrderByDescending(visit => visit.Last).Take(Slack).Select(visit => visit.Key).ToHashSet();
        var best = visits.Values.Where(visit => !kept.Contains(visit.Key))
            .OrderByDescending(visit => Score(visit, now)).Take(Cap - Slack).Select(visit => visit.Key).ToList();
        kept.UnionWith(best);
        foreach (var key in visits.Keys.Where(key => !kept.Contains(key)).ToList())
        {
            visits.Remove(key);
            if (pageText.Remove(key, out var old)) Credit(old, -old.Weight);
        }
        // Один раз, а не на каждую ушедшую страницу.
        visitedHosts = pageText.Values.Select(text => text.Host).Where(host => host.Length > 0).ToHashSet();
    }

    /// Место, где был другой браузер. Считается так, как считалось там: в день
    /// перехода поле уже знает вас. После серии вызовов — Settle.
    public void Take(Uri url, string title, long count, DateTime last)
    {
        if (!IsWeb(url)) return;
        url = Kept(url);
        var key = Identity(url);
        last = last.ToUniversalTime();
        lock (gate)
        {
            if (visits.TryGetValue(key, out var seen))
            {
                // Большее из двух, а не сумма: тот же браузер, принесённый ещё
                // раз, не должен удвоить каждый визит.
                seen.Count = Math.Min(MostVisits, Math.Max(seen.Count, count));
                if (last > seen.Last) seen.Last = last;
                if (seen.Title.Length == 0) seen.Title = title;
            }
            else
            {
                visits[key] = seen = new Visit
                {
                    Url = url.AbsoluteUri, Key = key, Title = title, Count = Math.Clamp(count, 0, MostVisits), Last = last,
                };
            }
            Remember(seen, url);
            Prune();
        }
    }

    public void Settle()
    {
        lock (gate) Save();
    }

    /// Заголовок страницы обычно приходит на мгновение позже самой страницы.
    public void Retitle(Uri url, string title)
    {
        if (title.Length == 0) return;
        var key = Identity(url);
        lock (gate)
        {
            if (!visits.TryGetValue(key, out var seen) || seen.Title == title) return;
            var first = seen.Title.Length == 0;
            seen.Title = title;
            Remember(seen);
            // Сразу пишется только первый заголовок. Тот, что меняется дальше
            // (почта считает письма), переписывал бы весь файл каждый раз: он
            // ждёт записи следующего визита или выхода.
            if (first) Save();
            else owed = true;
        }
    }

    public void Forget()
    {
        lock (gate)
        {
            visits.Clear();
            pageText.Clear();
            weights.Clear();
            visitedHosts.Clear();
            Save();
        }
    }

    public void Forget(string key)
    {
        lock (gate)
        {
            visits.Remove(key);
            ForgetText(key);
            Save();
        }
    }

    // MARK: чтение

    /// Всё, где вы были, сначала новое.
    public List<Trace> Everything(string matching = "")
    {
        var needle = matching.Trim().ToLowerInvariant();
        lock (gate)
        {
            List<Trace> found = [];
            foreach (var visit in visits.Values.OrderByDescending(visit => visit.Last))
            {
                var text = pageText.GetValueOrDefault(visit.Key);
                // Каждый визит засчитывается и главной странице сайта, чтобы
                // поле могло её предложить. У таких записей нет заголовка, и в
                // списке посещённого они были бы второй копией каждой строки.
                if (visit.Title.Length == 0 && text?.Homepage == true) continue;
                if (needle.Length > 0 && text?.Searchable.Contains(needle) != true
                    && !visit.Title.ToLowerInvariant().Contains(needle)) continue;
                if (!Uri.TryCreate(visit.Url, UriKind.Absolute, out var url)) continue;
                found.Add(new Trace(visit.Key, text?.Address ?? visit.Key, visit.Title, url, visit.Last, visit.Count));
            }
            return found;
        }
    }

    /// Строка страницы истории: имя дня или место под ним. First и Last — края
    /// карточки дня: по ним строка скругляет углы.
    public sealed record Line(string? Day, Trace? Page, bool First, bool Last);

    /// Список, как его рисует страница истории: имя каждого дня и под ним его
    /// места. Дни считаются по местному времени. traces — сначала новое, как
    /// их отдаёт Everything.
    public static List<Line> Lines(IReadOnlyList<Trace> traces, DateTime today)
    {
        List<Line> lines = [];
        for (var at = 0; at < traces.Count;)
        {
            var day = traces[at].Last.ToLocalTime().Date;
            var end = at;
            while (end < traces.Count && traces[end].Last.ToLocalTime().Date == day) end++;
            lines.Add(new Line(Named(day, today.Date), null, false, false));
            for (var i = at; i < end; i++) lines.Add(new Line(null, traces[i], i == at, i == end - 1));
            at = end;
        }
        return lines;
    }

    /// Ключи мест от from до to включительно, в порядке списка, без имён дней:
    /// Shift+щелчок на странице истории выбирает всё между прошлым выбранным
    /// и этим. Нет from в списке (его убрали или скрыл поиск) — только to.
    public static List<string> Between(IReadOnlyList<Line> lines, string from, string to)
    {
        var keys = lines.Where(line => line.Page is not null).Select(line => line.Page!.Key).ToList();
        var (start, end) = (keys.IndexOf(from), keys.IndexOf(to));
        if (end < 0) return [];
        if (start < 0) return [to];
        return keys.GetRange(Math.Min(start, end), Math.Abs(start - end) + 1);
    }

    // Интерфейс английский, поэтому и месяц пишется по-английски, а не на
    // языке системы. Год — только у чужого года.
    static string Named(DateTime day, DateTime today) =>
        day == today ? "Today"
        : day == today.AddDays(-1) ? "Yesterday"
        : day.ToString(day.Year == today.Year ? "d MMMM" : "d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// Лучшие совпадения первыми: счёт = совпадение × log(2 + популярность).
    public List<Suggestion> Suggestions(string typed, int limit = 5)
    {
        var needle = Stripped(typed);
        // Пустое поле ничего не предлагает: список догадок перед человеком,
        // который ещё не сказал, чего хочет, — это шум.
        if (needle.Length == 0 || limit <= 0) return [];

        var bytes = Encoding.UTF8.GetBytes(needle);
        var words = needle.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => Encoding.UTF8.GetBytes(word)).ToArray();

        lock (gate)
        {
            var fade = Math.Exp(-(DateTime.UtcNow - epoch).TotalSeconds / Fading);
            // Пока идёт просмотр, хранятся только лучшие несколько, по порядку.
            List<(Suggestion Made, double Score)> best = [];

            // Сначала больший счёт, потом более короткий ключ, потом кто пришёл раньше.
            bool Ahead(double score, string key, (Suggestion Made, double Score) other) =>
                score == other.Score
                    ? Encoding.UTF8.GetByteCount(key) < Encoding.UTF8.GetByteCount(other.Made.Key)
                    : score > other.Score;

            bool Wanted(double score, string key) => best.Count < limit || Ahead(score, key, best[^1]);

            void Place(Suggestion made, double score)
            {
                var at = 0;
                while (at < best.Count && !Ahead(score, made.Key, best[at])) at++;
                best.Insert(at, (made, score));
                if (best.Count > limit) best.RemoveAt(best.Count - 1);
            }

            void Visited(Visit visit, PageText text, double fit)
            {
                var popularity = Math.Max(0, weights.GetValueOrDefault(text.Trail[^1]) * fade);
                var score = fit * Math.Log(2 + popularity);
                if (Wanted(score, text.Address) && Uri.TryCreate(visit.Url, UriKind.Absolute, out var url))
                    Place(new Suggestion(text.Address, visit.Title, url, Suggestion.Kinds.Visited), score);
            }

            void Known(KnownSite site, double fit)
            {
                var score = fit * KnownWeight;
                if (Wanted(score, site.Host))
                    Place(new Suggestion(site.Host, site.Title, new Uri("https://" + site.Host), Suggestion.Kinds.Known), score);
            }

            foreach (var (key, text) in pageText)
            {
                if (!Holds(words, text.Findable)) continue;
                if (Fit(bytes, words, text.SearchableBytes, text.Findable) is not { } fit) continue;
                if (visits.TryGetValue(key, out var visit)) Visited(visit, text, fit);
            }
            foreach (var site in KnownSites)
            {
                if (visitedHosts.Contains(site.Host) || !Holds(words, site.Findable)) continue;
                if (Fit(bytes, words, site.HostBytes, site.Findable) is { } fit) Known(site, fit);
            }

            // Осталось место: опечатка, совпадение 1. Это самое медленное,
            // поэтому только теперь и только с трёх букв.
            if (best.Count < limit && needle.Length >= 3)
            {
                foreach (var (key, text) in pageText)
                    if (Fuzzy.Match(words, text.Findable) == 0 && visits.TryGetValue(key, out var visit))
                        Visited(visit, text, 1);
                foreach (var site in KnownSites)
                    if (!visitedHosts.Contains(site.Host) && Fuzzy.Match(words, site.Findable) == 0)
                        Known(site, 1);
            }

            return best.Select(entry => entry.Made).ToList();
        }
    }

    // Каждое слово где-то на странице.
    static bool Holds(byte[][] words, ReadOnlySpan<byte> findable)
    {
        foreach (var word in words)
            if (!Fuzzy.Contains(findable, word)) return false;
        return true;
    }

    // 4 — адрес начинается с набранного; 3 — с него начинается слово хоста;
    // 2 — каждое слово начинает слово заголовка или адреса; 1 — найдено
    // внутри слова. Заголовок и путь — с трёх букв: «ui» есть почти везде.
    static double? Fit(ReadOnlySpan<byte> needle, byte[][] words, ReadOnlySpan<byte> address, ReadOnlySpan<byte> findable)
    {
        if (address.StartsWith(needle)) return 4;
        var end = address.IndexOfAny((byte)'/', (byte)'?', (byte)'#');
        if (Fuzzy.StartsWord(needle, end < 0 ? address : address[..end])) return 3;
        if (needle.Length < 3) return null;
        foreach (var word in words)
            if (!Fuzzy.StartsWord(word, findable)) return 1;
        return 2;
    }

    /// Что поле рисует серым после каретки: остаток лучшего совпадения. Null,
    /// если оно не продолжает набранное.
    public static string? Completion(string typed, IReadOnlyList<Suggestion> options)
    {
        if (typed.Length == 0) return null;
        // Одна буква дописывает только верхнюю строку и только если это
        // главная страница сайта, где вы были: «x» становится x.com. Страница
        // внутри сайта, имя, которое программа просто знает, и строки ниже
        // ждут второй буквы.
        var single = typed.Length == 1;
        if (single && options.Count > 0 && options[0].Kind != Suggestion.Kinds.Visited) return null;
        foreach (var option in single ? options.Take(1) : options)
        {
            foreach (var (key, target) in Endings(option))
            {
                if (key.Length < typed.Length) continue;
                if (single && key.AsSpan().IndexOfAny('/', '?', '#') >= 0) continue;
                if (!key.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = key[typed.Length..];
                // Дописанное обязано вести на ту же страницу: регистр пути,
                // схема и порт должны получиться теми же.
                if (Address.Url(typed + rest) is not { } completed || Identity(completed) != target) continue;
                return rest.Length == 0 ? null : rest;
            }
        }
        return null;
    }

    // Чем строку можно дописать и какая страница должна получиться. Её адрес
    // как показан; а для сайта с www — ещё и имя без www: «app» по-прежнему
    // становится apple.com, а сайт сам отправит на свой www.
    static IEnumerable<(string Key, string Target)> Endings(Suggestion option)
    {
        yield return (option.Key, Identity(option.Url));
        if (option.Key.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            && option.Url.Host.StartsWith("www.", StringComparison.Ordinal))
        {
            var bare = new UriBuilder(option.Url) { Host = option.Url.Host[4..] }.Uri;
            yield return (option.Key[4..], Identity(bare));
        }
    }

    // Часто и недавно. Визит месячной давности весит около трети свежего:
    // примерно столько привычка перестаёт быть привычкой.
    static double Score(Visit visit, DateTime now) =>
        visit.Count * Math.Exp(-Math.Max(0, (now - visit.Last).TotalDays) / 30);

    // MARK: файл

    // ponytail: файл читается синхронно в конструкторе. Две тысячи записей —
    // это миллисекунды; если замер холодного старта покажет иное, читать в
    // фоне и накатывать изменения, пришедшие за время загрузки, как в апстриме.
    void Load()
    {
        byte[] data;
        try { data = File.ReadAllBytes(file); }
        // Только «файла нет» значит «истории нет»; см. Session.Read.
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return; }

        List<Visit>? list = null;
        try { list = JsonSerializer.Deserialize(data, Json.Default.ListVisit); }
        catch (JsonException) { }
        if (list is null)
        {
            Store.Quarantine(file);
            return;
        }

        var cleaned = false;
        foreach (var visit in list)
        {
            if (visit is null || !Uri.TryCreate(visit.Url, UriKind.Absolute, out var url) || !IsWeb(url)) continue;
            visit.Title ??= "";
            // Счётчик из правленого руками файла держим в разумных пределах.
            visit.Count = Math.Clamp(visit.Count, 0, MostVisits);
            visit.Last = visit.Last.ToUniversalTime();
            if (url.UserInfo.Length > 0)
            {
                // Адрес, сохранённый с именем и паролем, теряет их здесь.
                url = Kept(url);
                visit.Url = url.AbsoluteUri;
                cleaned = true;
            }
            // Ключам из файла не верим: они строятся заново из адреса, а
            // совпавшие записи сливаются.
            visit.Key = Identity(url);
            visits[visit.Key] = visits.TryGetValue(visit.Key, out var had) ? Merged(had, visit) : visit;
        }
        foreach (var visit in visits.Values) Remember(visit);
        // И сразу записать без паролей, а не при следующем визите.
        if (cleaned) Save();
    }

    // Одна страница дважды: визиты складываются, дата берётся последняя,
    // заголовок — у кого он есть.
    static Visit Merged(Visit a, Visit b)
    {
        var (kept, other) = a.Last >= b.Last ? (a, b) : (b, a);
        kept.Count = Math.Min(MostVisits, a.Count + b.Count);
        if (kept.Title.Length == 0) kept.Title = other.Title;
        return kept;
    }

    void Save()
    {
        owed = true;
        if (pausing) return;
        pausing = true;
        timer ??= new Timer(_ =>
        {
            lock (gate)
            {
                pausing = false;
                Write(now: false);
            }
        });
        timer.Change(SavePause, Timeout.InfiniteTimeSpan);
    }

    /// Выход из приложения: всё, чего ещё нет в файле, пишется до конца
    /// процесса, а не никогда.
    public void Flush()
    {
        lock (gate) Write(now: true);
    }

    void Write(bool now)
    {
        if (!owed) return;
        owed = false;
        var at = DateTime.UtcNow;
        // Копии: кодирование идёт в фоне, а записи тем временем меняются.
        var snapshot = visits.Values.Select(visit => visit.Copy()).ToList();
        // Предел, чтобы файл не рос без конца. Уходит то, что посещалось реже
        // всего и давнее всего.
        Disk.Write(file, () => JsonSerializer.SerializeToUtf8Bytes(
            snapshot.OrderByDescending(visit => Score(visit, at)).Take(Cap).ToList(), Json.Default.ListVisit), now);
    }

    sealed record KnownSite(string Host, string Title)
    {
        public readonly byte[] HostBytes = Encoding.UTF8.GetBytes(Host);
        public readonly byte[] Findable = Encoding.UTF8.GetBytes(Title.ToLowerInvariant() + " " + Host);
    }

    // С чего начать в первый день, пока истории нет. Ниже всего посещённого и
    // пропадает из списка, как только вы побывали там сами. Список апстрима.
    static readonly KnownSite[] KnownSites =
    [
        new("google.com", "Google"), new("mail.google.com", "Gmail"),
        new("drive.google.com", "Google Drive"), new("calendar.google.com", "Google Calendar"),
        new("maps.google.com", "Google Maps"), new("youtube.com", "YouTube"),
        new("github.com", "GitHub"), new("figma.com", "Figma"), new("vercel.com", "Vercel"),
        new("notion.so", "Notion"), new("linear.app", "Linear"), new("slack.com", "Slack"),
        new("discord.com", "Discord"), new("x.com", "X"), new("linkedin.com", "LinkedIn"),
        new("instagram.com", "Instagram"), new("reddit.com", "Reddit"),
        new("news.ycombinator.com", "Hacker News"), new("stackoverflow.com", "Stack Overflow"),
        new("claude.ai", "Claude"), new("chatgpt.com", "ChatGPT"),
        new("dribbble.com", "Dribbble"), new("behance.net", "Behance"),
        new("awwwards.com", "Awwwards"), new("mobbin.com", "Mobbin"),
        new("siteinspire.com", "SiteInspire"), new("are.na", "Are.na"),
        new("pinterest.com", "Pinterest"), new("framer.com", "Framer"),
        new("webflow.com", "Webflow"), new("developer.apple.com", "Apple Developer"),
        new("swift.org", "Swift"), new("npmjs.com", "npm"), new("supabase.com", "Supabase"),
        new("stripe.com", "Stripe"), new("shopify.com", "Shopify"),
        new("cloudflare.com", "Cloudflare"), new("netlify.com", "Netlify"),
        new("apple.com", "Apple"), new("spotify.com", "Spotify"), new("netflix.com", "Netflix"),
        new("wikipedia.org", "Wikipedia"), new("deepl.com", "DeepL"), new("loom.com", "Loom"),
        new("amazon.fr", "Amazon"), new("leboncoin.fr", "leboncoin"), new("lemonde.fr", "Le Monde"),
    ];
}
