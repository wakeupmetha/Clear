using System.Text.Json;

namespace Browser;

// Перенос Sources/Search/Session.swift, пока без групп, сплитов и пространств:
// их поля добавятся вместе с самими функциями, а незнакомые ключи при чтении
// пропускаются, так что файл с Mac читается уже сейчас.
//
// Что было открыто в прошлый раз: список адресов с названиями и какая вкладка
// была перед глазами. Остальное либо на странице, либо в истории рядом.
// Поэтому восстановление сессии не стоит ни одного процесса: движок вкладки
// создаётся, только когда на неё переключились.
public static class Session
{
    public sealed class Entry
    {
        public required string Url { get; set; }
        public required string Title { get; set; }
        /// Буква или значок закреплённой вкладки; null у обычной.
        public string? Pin { get; set; }
        /// Имя, которое вкладке дал человек.
        public string? Name { get; set; }
        /// Своя страница закреплённой вкладки: та, на которой её закрепили.
        public string? Home { get; set; }
        /// Когда с вкладки ушли в последний раз. Нужно для сна вкладок и для
        /// вкладок, которые закрываются сами: ночь, когда браузер был закрыт,
        /// тоже считается.
        public DateTime? Touched { get; set; }
        public bool Muted { get; set; }
    }

    public sealed class Shape
    {
        public required List<Entry> Tabs { get; set; }
        /// Номер вкладки перед глазами; -1, если такой нет.
        public required int Active { get; set; }
    }

    static string Location => Store.File("session.json");

    public static Shape Read()
    {
        byte[] data;
        try { data = File.ReadAllBytes(Location); }
        // Только «файла нет» значит «сессии нет». Занятый или недоступный файл
        // — это ошибка наверх: вернуть пустую сессию значило бы затереть
        // настоящую при следующем сохранении.
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Shape { Tabs = [], Active = 0 };
        }
        try
        {
            if (JsonSerializer.Deserialize(data, Json.Default.Shape) is { } shape) return shape;
        }
        catch (JsonException) { }
        // Файл есть, но не разбирается: это не то же, что нет файла.
        Store.Quarantine(Location);
        return new Shape { Tabs = [], Active = 0 };
    }

    /// now пишет в вызывающем потоке: сессия, отданная фоновой очереди на
    /// выходе, может так и не дойти до диска.
    public static void Write(Shape shape, bool now = false)
    {
        // Кодируем сразу: вызывающий может менять shape после возврата.
        var data = JsonSerializer.SerializeToUtf8Bytes(shape, Json.Default.Shape);
        Disk.Write(Location, () => data, now);
    }
}
