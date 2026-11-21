using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Browser;

// Настройки: то, что в апстриме лежит в UserDefaults (Store.settings). Здесь
// это один файл settings.json рядом с историей и сессией, поэтому у
// тестового мира настройки свои: проверка, которая меняет раскладку, не
// меняет её у человека.
//
// Имена ключей те же, что в апстриме («sidebar.folded», «shield.paused»).
// Значение не того типа читается как отсутствующее: правленый руками файл не
// должен ронять браузер.
public static class Settings
{
    static readonly Lock Gate = new();
    static JsonObject values = [];
    /// Из какого файла прочитано: папку хранилища могут сменить (Store.Use).
    static string? loaded;

    /// Настройку с этим ключом записали или удалили. Так о ней узнают все,
    /// кому она важна: окно, вкладки, страница настроек. Тот, кто её меняет,
    /// о них не знает. Приходит в потоке, который менял настройку.
    public static event Action<string>? Changed;

    public static bool Bool(string key, bool fallback = false)
    {
        lock (Gate) return Values()[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;
    }

    public static string? Text(string key)
    {
        lock (Gate) return Values()[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    public static double Number(string key)
    {
        lock (Gate) return Values()[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;
    }

    public static string[] List(string key)
    {
        lock (Gate)
        {
            return Values()[key] is JsonArray array
                ? [.. array.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>()]
                : [];
        }
    }

    public static void Set(string key, bool value) => Put(key, JsonValue.Create(value));

    public static void Set(string key, string value) => Put(key, JsonValue.Create(value));

    public static void Set(string key, double value) => Put(key, JsonValue.Create(value));

    public static void Set(string key, IEnumerable<string> list) =>
        Put(key, new JsonArray([.. list.Select(item => (JsonNode?)JsonValue.Create(item))]));

    /// Настройка возвращается к значению по умолчанию.
    public static void Remove(string key) => Put(key, null);

    static void Put(string key, JsonNode? value)
    {
        lock (Gate)
        {
            var all = Values();
            if (value is null) all.Remove(key);
            else all[key] = value;
            // Кодируем сразу: запись идёт в фоне, а значения тем временем меняются.
            var data = Encoding.UTF8.GetBytes(all.ToJsonString());
            Disk.Write(loaded!, () => data);
        }
        // Вне замка: слушатель в ответ читает настройки и может их менять.
        Changed?.Invoke(key);
    }

    static JsonObject Values()
    {
        var file = Store.File("settings.json");
        if (loaded == file) return values;
        loaded = file;
        return values = Read(file);
    }

    static JsonObject Read(string file)
    {
        byte[] data;
        try { data = File.ReadAllBytes(file); }
        // Только «файла нет» значит «настроек нет»; см. Session.Read.
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return []; }
        try
        {
            if (JsonNode.Parse(data) is JsonObject read) return read;
        }
        catch (JsonException) { }
        Store.Quarantine(file);
        return [];
    }
}
