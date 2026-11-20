namespace Browser.Tests;

static class ExtensionsTests
{
    public static void Run()
    {
        Check.World("extensions");
        var source = Path.Combine(Check.Root, "my extension!");
        Directory.CreateDirectory(Path.Combine(source, "js"));
        File.WriteAllText(Path.Combine(source, "manifest.json"), """
            {
              // комментарий, как в манифестах, которые Chrome принимает
              "name": "__MSG_name__", "version": "1.2",
              "options_page": "old.html",
              "options_ui": { "page": "options.html" },
            }
            """);
        File.WriteAllText(Path.Combine(source, "js", "main.js"), "1");

        Check.Expect(Extensions.Manifest(source) == ("__MSG_name__", "1.2", "options.html"),
            "манифест с комментарием и лишней запятой читается, options_ui важнее options_page");
        Check.Expect(Extensions.Manifest(Check.Root) is null, "папка без manifest.json — не расширение");
        var bare = Path.Combine(Check.Root, "bare");
        Directory.CreateDirectory(bare);
        File.WriteAllText(Path.Combine(bare, "manifest.json"), """{ "name": "Bare", "version": "2", "options_ui": "page.html" }""");
        Check.Expect(Extensions.Manifest(bare) == ("Bare", "2", null), "options_ui не того вида не роняет чтение");
        Check.Expect(Extensions.Button(bare, 16) is null, "без action — без кнопки");
        File.WriteAllText(Path.Combine(bare, "manifest.json"), """
            { "name": "Bare", "version": "2", "icons": { "48": "i48.png", "128": "i128.png" },
              "action": { "default_popup": "pop.html", "default_title": "Do it", "default_icon": { "16": "a16.png", "32": "a32.png", "x": "bad.png" } } }
            """);
        Check.Expect(Extensions.Button(bare, 16) == new Knob("pop.html", "Do it", "a16.png"), "action: окно, подпись, значок ровно своего размера");
        Check.Expect(Extensions.Button(bare, 20)?.Icon == "a32.png", "значок — наименьший не меньше нужного");
        Check.Expect(Extensions.Button(bare, 64)?.Icon == "a32.png", "все меньше нужного — самый крупный");
        File.WriteAllText(Path.Combine(bare, "manifest.json"), """
            { "name": "Bare", "version": "2", "icons": { "48": "i48.png", "128": "i128.png" }, "browser_action": { "default_icon": "one.png" } }
            """);
        Check.Expect(Extensions.Button(bare, 16) == new Knob(null, null, "one.png"), "browser_action (MV2), значок строкой, окна нет");
        File.WriteAllText(Path.Combine(bare, "manifest.json"), """
            { "name": "Bare", "version": "2", "icons": { "48": "i48.png", "128": "i128.png" }, "action": {} }
            """);
        Check.Expect(Extensions.Button(bare, 16)?.Icon == "i48.png", "без default_icon — значок из icons");
        File.WriteAllText(Path.Combine(bare, "manifest.json"), """{ "name": "Bare" }""");
        Check.Expect(Extensions.Manifest(bare) is null, "без версии — не расширение");

        var copy = Extensions.Stage(source);
        Check.Expect(Path.GetDirectoryName(copy) == Extensions.Root && Path.GetFileName(copy) == "myextension",
            "копия ложится в Extensions под именем папки без лишних знаков");
        Check.Expect(File.ReadAllText(Path.Combine(copy, "js", "main.js")) == "1", "вложенные папки копируются");
        Check.Expect(Path.GetFileName(Extensions.Stage(source + Path.DirectorySeparatorChar)) == "myextension-2",
            "вторая копия той же папки не затирает первую");
        var refused = false;
        try { Extensions.Stage(bare); }
        catch (InvalidDataException) { refused = true; }
        Check.Expect(refused, "папку без манифеста не копируем");
        refused = false;
        File.WriteAllText(Store.File("manifest.json"), """{ "name": "Self", "version": "1" }""");
        try { Extensions.Stage(Store.Folder); }
        catch (InvalidDataException) { refused = true; }
        Check.Expect(refused && !Directory.Exists(Path.Combine(Extensions.Root, "extensions")),
            "папку, где лежит само хранилище, не копируем в себя");

        Check.Expect(Extensions.All().Count == 0, "без installed.json список пуст");
        Extensions.Keep(new Installed("aaa", "First", "1.2", "myextension", "options.html", source));
        Extensions.Keep(new Installed("bbb", "Second", "1", "myextension-2", null, source));
        Extensions.Keep(new Installed("aaa", "First", "1.3", "myextension", "options.html", source));
        Check.Expect(Extensions.All() is [{ Id: "bbb" }, { Id: "aaa", Version: "1.3" }], "запись с тем же id заменяет прежнюю");

        Extensions.Forget("aaa");
        Check.Expect(Extensions.All() is [{ Id: "bbb" }] && !Directory.Exists(copy) && Directory.Exists(source),
            "убранное расширение уходит из списка вместе с копией, папка человека цела");

        // Список правили руками: стирать можно только внутри Extensions.
        var outside = Path.Combine(Check.Root, "outside");
        Directory.CreateDirectory(outside);
        Extensions.Keep(new Installed("ccc", "Evil", "1", Path.Combine("..", "..", "outside"), null, null));
        Extensions.Keep(new Installed("ddd", "Dots", "1", "..", null, null));
        Extensions.Forget("ccc");
        Extensions.Forget("ddd");
        Check.Expect(Directory.Exists(outside) && Directory.Exists(Store.Folder) && Extensions.All() is [{ Id: "bbb" }],
            "имя копии с путём наружу ничего вне Extensions не стирает");

        File.WriteAllText(Path.Combine(Extensions.Root, "installed.json"), "[{");
        Check.Expect(Extensions.All().Count == 0 && Directory.GetFiles(Extensions.Root, "installed.unreadable-*.json").Length == 1,
            "нечитаемый installed.json отложен в сторону");
    }
}
