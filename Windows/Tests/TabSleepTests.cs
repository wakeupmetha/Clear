namespace Browser.Tests;

static class TabSleepTests
{
    public static void Run()
    {
        Check.Expect(TabSleep.Wait(true, null, 0) == TimeSpan.FromMinutes(30) && TabSleep.Wait(true, "later", 0) == TimeSpan.FromHours(2)
            && TabSleep.Wait(true, "sooner", 0) == TimeSpan.FromMinutes(10), "Normal — полчаса, Later — два часа, Sooner — десять минут");
        Check.Expect(TabSleep.Wait(false, "sooner", 5) is null, "выключенный сон не включают ни уровень, ни sleep.after");
        Check.Expect(TabSleep.Wait(true, "later", 3) == TimeSpan.FromSeconds(3), "sleep.after важнее уровня");
        Check.Expect(TabSleep.Choice(false, "later") == "never" && TabSleep.Choice(true, null) == "normal"
            && TabSleep.Choice(true, "sooner") == "sooner" && TabSleep.Choice(true, "nonsense") == "normal", "выбор в настройках: never, уровень, незнакомый — normal");
        Check.Expect(TabSleep.Every(TimeSpan.FromSeconds(3)) == TimeSpan.FromSeconds(5) && TabSleep.Every(TimeSpan.FromMinutes(30)) == TimeSpan.FromMinutes(1)
            && TabSleep.Every(TimeSpan.FromSeconds(100)) == TimeSpan.FromSeconds(25) && TabSleep.Every(null) == TimeSpan.FromMinutes(1),
            "смотрим раз в четверть ожидания, от 5 с до минуты");
        var now = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        Check.Expect(TabSleep.Due(now.AddMinutes(-30), now, TimeSpan.FromMinutes(30)) && !TabSleep.Due(now.AddMinutes(-29), now, TimeSpan.FromMinutes(30))
            && !TabSleep.Due(null, now, TimeSpan.Zero), "пора — когда ожидание прошло; без времени — никогда");
    }
}
