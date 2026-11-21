namespace Browser.Tests;

static class BookmarksTests
{
    /// Как перезапуск: другой мир и обратно. Список читается заново, только
    /// когда сменилась папка хранилища, поэтому в другом мире его надо спросить.
    static void Reopen()
    {
        Disk.Drain();
        Check.World("bookmarks-elsewhere");
        _ = Bookmarks.Roots;
        Check.World("bookmarks");
    }

    public static void Run()
    {
        Check.World("bookmarks");
        Check.Expect(Bookmarks.Roots.Count == 0 && Bookmarks.Sites().Count == 0, "без файла закладок нет");
        var docs = Bookmarks.Add(new Uri("https://docs.example.com/guide"), "Guide");
        Bookmarks.Add(new Uri("https://www.example.org/"), "  ");
        Check.Expect(docs is not null && Bookmarks.Roots is [{ Title: "Guide" }, { Label: "example.org" }],
            "новая — в конец; без заголовка имя — адрес, как его пишет поле");
        Check.Expect(Bookmarks.Add(new Uri("https://docs.example.com/guide"), "Again") is null && Bookmarks.Roots.Count == 2,
            "та же страница второй раз не сохраняется");
        Check.Expect(Bookmarks.Find(new Uri("https://docs.example.com/guide"))?.Id == docs!.Id && Bookmarks.Find(new Uri("https://docs.example.com/")) is null,
            "найти — по точному адресу");

        // Файл с Mac: папки, ключи апстрима, id в верхнем регистре.
        Disk.Drain();
        File.WriteAllText(Store.File("bookmarks.json"), """
            [{"id":"E621E1F8-C36C-495A-93FC-0C247A3E6E5F","title":"Work","children":[
              {"id":"5B1D7C3A-1F7E-4C1B-9A55-2D7C6E1B9F10","title":"Tracker","url":"https://tracker.example.com/board"},
              {"id":"0C9F5E2B-8A6D-4F3E-B1C7-4E2A9D8B7C61","title":"Deep","children":[
                {"id":"7A3E9C1D-2B4F-4E6A-8C5D-1F9B3E7A2D40","title":"Wiki","url":"https://wiki.example.com/"}]}]},
             {"id":"9D8C7B6A-5F4E-4D3C-2B1A-0F9E8D7C6B5A","title":"News","url":"https://news.example.net/"}]
            """);
        Reopen();
        var sites = Bookmarks.Sites();
        Check.Expect(sites.Count == 3 && sites[0].Node.Title == "Tracker" && sites[0].Path is ["Work"]
            && sites[1].Path is ["Work", "Deep"] && sites[2].Path.Length == 0, "файл с Mac читается с папками, сайты по порядку и с путём");
        Check.Expect(Bookmarks.Matches("WIKI example").Select(site => site.Node.Title).SequenceEqual(["Wiki"])
            && Bookmarks.Matches("example").Count == 3 && Bookmarks.Matches("  ").Count == 3 && Bookmarks.Matches("tracker wiki").Count == 0,
            "поиск: все слова, в любом порядке и регистре, в имени или адресе");
        Bookmarks.Remove(Guid.Parse("7A3E9C1D-2B4F-4E6A-8C5D-1F9B3E7A2D40"));
        Check.Expect(Bookmarks.Sites().Count == 2 && Bookmarks.Roots[0].Children!.Count == 2, "убрать — и из папки; папка остаётся");
        Reopen();
        Check.Expect(Bookmarks.Sites().Count == 2 && Bookmarks.Find(new Uri("https://tracker.example.com/board"))?.Id == Guid.Parse("5B1D7C3A-1F7E-4C1B-9A55-2D7C6E1B9F10"),
            "после перезапуска — то же и те же id");

        File.WriteAllText(Store.File("bookmarks.json"), "not json");
        Reopen();
        Check.Expect(Bookmarks.Roots.Count == 0, "испорченный файл — пустой список, а файл отложен в сторону");
    }
}
