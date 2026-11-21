namespace Browser;

// Сон вкладок (Sleep.swift апстрима): вкладка, на которую давно не смотрели,
// отдаёт движок. Ключи апстрима: tabs.sleep (выключатель, по умолчанию
// включён), tabs.sleep.pace (later, normal, sooner), sleep.after — секунды
// вместо выбранного, для проверок и замеров.
public static class TabSleep
{
    /// Сколько вкладка ждёт без взгляда, прежде чем уснуть; null — не спит.
    /// Три уровня, а не минуты, как в апстриме: человек решает, насколько ему
    /// мешает просыпающаяся вкладка, а не сколько минут.
    public static TimeSpan? Wait(bool on, string? pace, double after) =>
        !on ? null
        : after > 0 ? TimeSpan.FromSeconds(after)
        : pace switch
        {
            "later" => TimeSpan.FromHours(2),
            "sooner" => TimeSpan.FromMinutes(10),
            _ => TimeSpan.FromMinutes(30),
        };

    /// Выбор в настройках (SleepChoice апстрима): never или уровень.
    public static string Choice(bool on, string? pace) => !on ? "never" : pace is "later" or "sooner" ? pace : "normal";

    /// Как часто смотреть, кому пора: четверть ожидания, от 5 с до минуты.
    public static TimeSpan Every(TimeSpan? wait) =>
        TimeSpan.FromSeconds(Math.Clamp((wait ?? TimeSpan.FromHours(1)).TotalSeconds / 4, 5, 60));

    /// Пора ли спать вкладке, оставленной в touched. Без времени — не пора:
    /// на ней ещё не были или её не оставляли.
    public static bool Due(DateTime? touched, DateTime now, TimeSpan wait) => touched is { } left && now - left >= wait;
}
