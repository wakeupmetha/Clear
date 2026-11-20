using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Browser.Tests;

// Значки сайтов: адрес сайта, годность файла, кэш на диске и один запрос на сайт
// за сессию. В сеть проверки ходят только на свой сервер на 127.0.0.1.
static class IconsTests
{
    public static async Task Run()
    {
        Check.World("icons");
        Sites();
        Kinds();
        Stored();
        Pruned();
        await Network();
    }

    static void Sites()
    {
        Check.Expect(Icons.Site(new Uri("https://www.YouTube.com/watch?v=1")) == "youtube.com", "www и регистр не отличают сайт");
        Check.Expect(Icons.Site(new Uri("http://localhost:3000/a")) == "localhost:3000", "порт не по умолчанию входит в сайт");
        Check.Expect(Icons.Site(new Uri("https://example.com:443/")) == "example.com", "порт 443 для https не входит в сайт");
        Check.Expect(Icons.Site(new Uri("http://example.com:8080/")) == "example.com:8080", "порт 8080 для http входит в сайт");
        Check.Expect(Icons.Site(new Uri("browser://settings")) is null, "внутренние страницы сайта не имеют");
        Check.Expect(Icons.Site(new Uri("file:///C:/x.txt")) is null, "файлы с диска сайта не имеют");

        var withWww = Icons.Site(new Uri("https://www.youtube.com/"));
        var bare = Icons.Site(new Uri("https://youtube.com/"));
        Check.Expect(withWww is not null && Icons.File(withWww) == Icons.File(bare!), "www и без www — один файл значка");
        var name = Path.GetFileName(Icons.File(bare!));
        Check.Expect(name.Length == 37 && name.EndsWith(".icon") && name[..32].All(char.IsAsciiHexDigitLower),
            "имя файла значка — 32 hex-символа и .icon");
    }

    static void Kinds()
    {
        var png = Filled(100, 0, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ico = Filled(100, 0, [0x00, 0x00, 0x01, 0x00]);
        var webp = Filled(100, 0, Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBP"));
        Check.Expect(Icons.Usable(png) && Icons.Usable(ico) && Icons.Usable(webp), "PNG, ICO и WebP по заголовку годятся");
        Check.Expect(!Icons.Usable(Filled(200, (byte)' ', Encoding.ASCII.GetBytes("<!doctype html>"))), "HTML не значок");
        Check.Expect(!Icons.Usable(Filled(200, (byte)' ', Encoding.ASCII.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\""))), "SVG не значок");
        Check.Expect(!Icons.Usable(Filled(20, 0, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])), "короче 60 байт не значок");
        Check.Expect(!Icons.Usable(Filled(1_048_577, 0, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])), "больше мегабайта не значок");
    }

    static void Stored()
    {
        var png = Filled(100, 0, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Check.Expect(Icons.Keep(new Uri("https://www.youtube.com/"), png), "годный значок сохраняется");
        // Окно читает значок сразу после Keep, без ожидания очереди записи.
        Check.Expect(Icons.Find(new Uri("https://youtube.com/")) is not null, "значок виден сразу после Keep");
        Disk.Drain();
        var found = Icons.Find(new Uri("https://youtube.com/x"));
        Check.Expect(found is not null && System.IO.File.ReadAllBytes(found).SequenceEqual(png),
            "значок находят для другой страницы того же сайта, байты те же");

        var html = Filled(200, (byte)' ', Encoding.ASCII.GetBytes("<!doctype html>"));
        Check.Expect(!Icons.Keep(new Uri("https://example.org/"), html), "HTML не сохраняется");
        Check.Expect(Icons.Find(new Uri("https://example.org/")) is null, "и файла для него нет");
    }

    static void Pruned()
    {
        // Пишем при прежнем большом пределе, иначе Keep сам обрезал бы папку на
        // середине записи и проверка порядка по времени стала бы недетерминированной.
        var png = Filled(100, 0, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        for (var i = 0; i < 10; i++) Icons.Keep(new Uri($"https://s{i}.test/"), png);
        Icons.Most = 8;
        for (var i = 0; i < 10; i++)
            System.IO.File.SetLastWriteTimeUtc(Icons.File(Icons.Site(new Uri($"https://s{i}.test/"))!), DateTime.UtcNow.AddMinutes(-100 + i));
        Icons.Prune();

        // Со значком youtube из предыдущей проверки файлов 11, режем до 6.
        Check.Expect(Directory.GetFiles(Icons.Folder, "*.icon").Length == 6, "после обрезки остаётся предел в три четверти");
        Check.Expect(Icons.Find(new Uri("https://s9.test/")) is not null, "самый новый значок остался");
        Check.Expect(Icons.Find(new Uri("https://s0.test/")) is null, "самый старый значок удалён");
        Icons.Most = 4000;
    }

    static async Task Network()
    {
        var body = Filled(120, 0, [0x00, 0x00, 0x01, 0x00]);
        var found = new FakeSite(Respond("200 OK", body));
        var missing = new FakeSite(Respond("404 Not Found", []));
        var slow = new FakeSite(Respond("200 OK", body), wait: 300);
        try
        {
            var page = new Uri($"http://127.0.0.1:{found.Port}/some/page?q=1");
            Check.Expect(await Icons.Ensure(page), "значок сайта без значка скачивается");
            Check.Expect(found.RequestLine.StartsWith("GET /favicon.ico "), "просим /favicon.ico у корня сайта");
            Check.Expect(!found.SawCookie, "куки в запросе нет");
            Disk.Drain();
            Check.Expect(Icons.Find(page) is not null, "значок лежит на диске после ожидания");
            Check.Expect(await Icons.Ensure(page) && found.Connections == 1, "второй раз берётся с диска, в сеть не идём");

            var other = new Uri($"http://127.0.0.1:{missing.Port}/");
            Check.Expect(!await Icons.Ensure(other), "404 значит, что значка нет");
            Check.Expect(!await Icons.Ensure(other) && missing.Connections == 1, "тот же сайт в эту сессию второй раз не спрашиваем");

            Icons.ForgetAll();
            Check.Expect(!Directory.Exists(Icons.Folder), "забыть историю значит убрать папку значков");
            Check.Expect(Icons.Find(page) is null, "после удаления значка нет");

            var late = new Uri($"http://127.0.0.1:{slow.Port}/");
            var asking = Icons.Ensure(late);
            Icons.ForgetAll();
            Check.Expect(!await asking && Icons.Find(late) is null, "значок, за которым пошли до Clear History, после него не ложится");
        }
        finally
        {
            found.Dispose();
            missing.Dispose();
            slow.Dispose();
        }
    }

    static byte[] Respond(string status, byte[] body)
    {
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: image/x-icon\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        return head.Concat(body).ToArray();
    }

    /// Байты, начинающиеся с head, дополненные до length байтами fill.
    static byte[] Filled(int length, byte fill, byte[] head)
    {
        var data = Enumerable.Repeat(fill, length).ToArray();
        head.CopyTo(data, 0);
        return data;
    }

    /// Свой сервер на 127.0.0.1: считает соединения, запоминает первую строку
    /// запроса и отвечает готовым ответом.
    sealed class FakeSite : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly byte[] reply;
        readonly int wait;
        int connections;

        public string RequestLine = "";
        public bool SawCookie;
        public int Connections => Volatile.Read(ref connections);
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        /// wait — сколько миллисекунд сайт думает перед ответом.
        public FakeSite(byte[] reply, int wait = 0)
        {
            this.reply = reply;
            this.wait = wait;
            listener.Start();
            _ = Serve();
        }

        async Task Serve()
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                // Dispose остановил слушатель: сервер завершился.
                catch (Exception) { return; }
                await Answer(client);
            }
        }

        async Task Answer(TcpClient client)
        {
            using var socket = client;
            try
            {
                var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                Interlocked.Increment(ref connections);
                var first = true;
                string? line;
                while ((line = await reader.ReadLineAsync()) is { Length: > 0 })
                {
                    if (first) { RequestLine = line; first = false; }
                    if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) SawCookie = true;
                }
                await Task.Delay(wait);
                await stream.WriteAsync(reply);
            }
            // Оборванное соединение проверке не мешает: считаем его как пришедшее.
            catch (IOException) { }
        }

        public void Dispose() => listener.Stop();
    }
}
