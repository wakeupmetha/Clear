namespace Browser.Tests;

static class DownloadsTests
{
    public static void Run()
    {
        Check.World("downloads");
        Check.Expect(Downloads.Kept.Count == 0, "без файла список пуст");
        var day = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        Keep Made(int n) => new() { Name = $"file{n}.zip", From = "example.com", Path = $@"C:\Downloads\file{n}.zip", Date = day.AddMinutes(n) };
        Downloads.Add(Made(1));
        Downloads.Add(Made(2));
        Check.Expect(Downloads.Kept is [{ Name: "file2.zip" }, { Name: "file1.zip" }], "новые первыми");
        Downloads.Add(Made(1));
        Check.Expect(Downloads.Kept is [{ Name: "file1.zip" }, { Name: "file2.zip" }], "тот же путь не повторяется, а встаёт наверх");
        for (var n = 3; n <= 60; n++) Downloads.Add(Made(n));
        Check.Expect(Downloads.Kept.Count == Downloads.Most && Downloads.Kept[0].Name == "file60.zip", "не больше пятидесяти, уходят старые");
        Downloads.Forget(@"C:\Downloads\file60.zip");
        Check.Expect(Downloads.Kept[0].Name == "file59.zip", "забыть — убрать из списка");

        Disk.Drain();
        Check.World("downloads-elsewhere");
        Check.Expect(Downloads.Kept.Count == 0, "у другого мира список свой");
        Check.World("downloads");
        Check.Expect(Downloads.Kept.Count == Downloads.Most - 1 && Downloads.Kept[0].Date == day.AddMinutes(59), "список и даты переживают перезапуск");
        Downloads.ForgetAll();
        Check.Expect(Downloads.Kept.Count == 0, "очистить — пустой список");

        Check.Expect(Downloads.Address("https://user:secret@example.com/a.zip?x=1") == "https://example.com/a.zip?x=1"
            && Downloads.Address("blob:https://example.com/1") is null && Downloads.Address("data:text/plain,hi") is null
            && Downloads.Address(null) is null, "адрес — только http(s), без имени и пароля");
        Check.Expect(Downloads.Size(1) == "1 byte" && Downloads.Size(999) == "999 bytes" && Downloads.Size(2048) == "2 KB"
            && Downloads.Size(3_260_000) == "3.3 MB" && Downloads.Size(12_400_000) == "12 MB" && Downloads.Size(999_600) == "1 MB",
            "размер — в тысячах, с десятой долей до десяти");
    }
}
