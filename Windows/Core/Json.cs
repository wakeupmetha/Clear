using System.Text.Json;
using System.Text.Json.Serialization;

namespace Browser;

// Формат файлов тот же, что у версии для macOS: те же имена ключей и те же
// даты. Поэтому history.json и session.json с Mac читаются здесь как есть, а
// обмен через общую папку между Mac и Windows не потребует конвертера.
//
// Генерация кода вместо рефлексии: это условие Native AOT.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = new[] { typeof(AppleDate) })]
[JsonSerializable(typeof(Session.Shape))]
[JsonSerializable(typeof(List<Visit>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(string[][]))]
[JsonSerializable(typeof(List<Installed>))]
internal sealed partial class Json : JsonSerializerContext;

/// Swift пишет дату числом секунд от 1 января 2001 года UTC.
internal sealed class AppleDate : JsonConverter<DateTime>
{
    static readonly DateTime Reference = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        Reference.AddSeconds(reader.GetDouble());

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((value.ToUniversalTime() - Reference).TotalSeconds);
}
