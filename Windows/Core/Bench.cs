using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Browser;

// Транспорт тест-сокета из Sources/Search/Bench.swift.
//
// Протокол тот же: запрос — одна строка JSON ({"do": "tabs", ...}), ответ —
// одна строка JSON. Транспорт другой: в апстриме Unix-сокет bench.sock, но у
// Python на Windows нет AF_UNIX, поэтому здесь именованный канал. Клиент на
// Python открывает его как файл: open(r"\\.\pipe\<имя>", "r+b", buffering=0),
// пишет строку, читает до "\n" и закрывает. Если канал занят, клиент
// повторяет попытку.
//
// Сами команды — дело приложения: они требуют живых вкладок.
public static class Bench
{
    /// Имя канала: по папке хранилища, так что у каждого тестового мира своё.
    public static string Pipe => Path.GetFileName(Store.Folder) + ".bench";

    /// Отвечает на запросы, пока не остановят. answer вызывается в фоновом
    /// потоке: переход в поток интерфейса — забота приложения.
    public static async Task Serve(string pipe, Func<JsonElement, Task<JsonNode?>> answer, CancellationToken stop)
    {
        // CurrentUserOnly: как и сокет апстрима, канал доступен только своему
        // пользователю.
        NamedPipeServerStream Listen() => new(pipe, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var waiting = Listen();
        try
        {
            while (true)
            {
                await waiting.WaitForConnectionAsync(stop);
                await using var client = waiting;
                // Следующий слушатель есть раньше, чем мы ответим этому
                // клиенту: иначе быстрый второй запрос не нашёл бы канал.
                waiting = Listen();
                // ponytail: отвечаем по одному; параллельные запросы — когда
                // появится команда, которая долго ждёт.
                try { await Reply(client, answer, stop); }
                catch (IOException) { }   // клиент ушёл посреди ответа
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }   // клиент молчит
            }
        }
        catch (OperationCanceledException) { }
        finally { await waiting.DisposeAsync(); }
    }

    static async Task Reply(NamedPipeServerStream client, Func<JsonElement, Task<JsonNode?>> answer, CancellationToken stop)
    {
        // Клиент, который подключился и замолчал, не должен держать сокет вечно.
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(stop);
        patience.CancelAfter(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
        var line = await reader.ReadLineAsync(patience.Token);

        JsonNode? reply;
        try
        {
            using var request = JsonDocument.Parse(line ?? "{}");
            patience.CancelAfter(Timeout.InfiniteTimeSpan);   // команда может ждать загрузки страницы
            reply = await answer(request.RootElement);
        }
        catch (JsonException) { reply = new JsonObject { ["error"] = "not json" }; }
        // Ошибка в одной команде — это ответ с ошибкой, а не конец сокета.
        catch (Exception e) when (e is not OperationCanceledException) { reply = new JsonObject { ["error"] = e.Message }; }

        await client.WriteAsync(Encoding.UTF8.GetBytes((reply?.ToJsonString() ?? "{}") + "\n"), stop);
        // Закрыть канал раньше, чем клиент дочитал, значит потерять ответ:
        // ждём, пока он закроет свою сторону.
        patience.CancelAfter(TimeSpan.FromSeconds(5));
        await reader.ReadLineAsync(patience.Token);
    }
}
