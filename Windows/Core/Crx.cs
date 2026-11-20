using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Browser;

// Расширения прямо из Chrome Web Store. Перенос Sources/Search/Crx.swift.
//
// Расширение там — .crx: zip с подписанным заголовком впереди. Он берётся по
// открытому адресу обновлений, который спрашивает любой Chromium, заголовок
// проверяется, zip распаковывается в папку браузера (Extensions.Unpack).
//
// Главная проверка: id расширения в Chrome — не имя, которое кто-то выбрал, а
// первые 16 байт SHA-256 его открытого ключа, записанные буквами от a до p.
// Поэтому в заголовке должен быть ключ, который даёт запрошенный id, и подпись
// этим ключом над пришедшим zip. Файл, изменённый по дороге, или расширение
// под чужим id не проходят одно или другое.
public static partial class Crx
{
    const string NotCrx = "What came back isn't a Chrome extension";
    const string Unsigned = "The extension's signature doesn't hold up";

    [GeneratedRegex("(?<![a-z])([a-p]{32})(?![a-z])")]
    private static partial Regex IdPattern();

    /// 32 буквы от a до p, где бы они ни стояли: голый id, ссылка магазина,
    /// старая ссылка chrome.google.com/webstore.
    public static string? Id(string text) =>
        IdPattern().Match(text.ToLowerInvariant()) is { Success: true } found ? found.Groups[1].Value : null;

    [GeneratedRegex("^/detail/(?:[^/]+/)?([a-p]{32})(?:/.*)?$")]
    private static partial Regex StorePath();

    [GeneratedRegex("^/webstore/detail/(?:[^/]+/)?([a-p]{32})(?:/.*)?$")]
    private static partial Regex OldStorePath();

    /// Расширение, о котором страница магазина: id из её пути /detail/ и только
    /// оттуда — показанное там расширение и есть оно. Только на самом магазине,
    /// по https и без имени перед хостом (storeID апстрима).
    public static string? StoreId(Uri? url)
    {
        if (url is not { Scheme: "https", UserInfo: "" }) return null;
        var found = url.Host switch
        {
            "chromewebstore.google.com" => StorePath().Match(url.AbsolutePath),
            "chrome.google.com" => OldStorePath().Match(url.AbsolutePath),
            _ => null,
        };
        return found is { Success: true } ? found.Groups[1].Value : null;
    }

    /// Адрес, по которому магазин отдаёт .crx. chrome — версия, которой
    /// представляемся: расширения с минимальной версией магазин старому
    /// браузеру не отдаёт.
    public static Uri DownloadUrl(string id, string chrome) => new(
        "https://clients2.google.com/service/update2/crx?response=redirect&acceptformat=crx3"
        + $"&prodversion={Uri.EscapeDataString(chrome)}&x={Uri.EscapeDataString($"id={id}&installsource=ondemand&uc")}");

    /// zip из CRX3, когда подпись сверена с id. Иначе InvalidDataException.
    public static byte[] VerifiedZip(byte[] crx, string id)
    {
        if (crx.Length <= 12 || !crx.AsSpan(0, 4).SequenceEqual("Cr24"u8) || BinaryPrimitives.ReadUInt32LittleEndian(crx.AsSpan(4)) != 3)
            throw new InvalidDataException(NotCrx);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(crx.AsSpan(8));
        if (size > crx.Length - 12) throw new InvalidDataException(NotCrx);
        var header = crx[12..(12 + (int)size)];
        var zip = crx[(12 + (int)size)..];

        // CrxFileHeader: 2 — доказательства sha256_with_rsa, 10000 —
        // signed_header_data (SignedData: 1 — crx_id).
        var fields = Protobuf(header);
        if (Field(fields, 10000) is not { } signed || Field(Protobuf(signed), 1) is not { } crxId || Letters(crxId) != id)
            throw new InvalidDataException(Unsigned);

        // Что подписано: постоянная приставка, подписанный заголовок и zip.
        var message = new byte[20 + signed.Length + zip.Length];
        "CRX3 SignedData\0"u8.CopyTo(message);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(16), (uint)signed.Length);
        signed.CopyTo(message, 20);
        zip.CopyTo(message, 20 + signed.Length);

        // Одно RSA-доказательство, чей ключ даёт id и чья подпись сходится.
        // Магазин добавляет своё; оно тоже может сходиться, но в счёт не идёт.
        var owns = fields.Where(field => field.Number == 2).Select(field => Protobuf(field.Value)).Any(proof =>
            Field(proof, 1) is { } key && Field(proof, 2) is { } signature
            && Letters(SHA256.HashData(key).AsSpan(0, 16)) == id && Holds(key, signature, message));
        if (!owns) throw new InvalidDataException(Unsigned);
        return zip;
    }

    /// Буквы id: каждая половина байта — буква от a (0) до p (15).
    public static string Letters(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length * 2);
        foreach (var one in bytes) text.Append((char)('a' + (one >> 4))).Append((char)('a' + (one & 15)));
        return text.ToString();
    }

    /// Открытый ключ RSA, как его пишет Chrome (SubjectPublicKeyInfo в DER),
    /// против подписи PKCS#1 v1.5 с SHA-256.
    static bool Holds(byte[] key, byte[] signature, byte[] message)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(key, out _);
            return rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException) { return false; }
    }

    static byte[]? Field(List<(int Number, byte[] Value)> fields, int number) =>
        fields.Where(field => field.Number == number).Select(field => field.Value).FirstOrDefault();

    /// Только поля с длиной: больше в заголовке CRX ничего нет.
    static List<(int Number, byte[] Value)> Protobuf(byte[] bytes)
    {
        var fields = new List<(int, byte[])>();
        var at = 0;
        long? Varint()
        {
            long value = 0;
            for (var shift = 0; shift <= 56 && at < bytes.Length; shift += 7)
            {
                var one = bytes[at++];
                value |= (long)(one & 0x7f) << shift;
                if ((one & 0x80) == 0) return value;
            }
            return null;
        }
        while (at < bytes.Length && Varint() is { } key)
        {
            switch (key & 7)
            {
                case 2:
                    if (Varint() is not { } length || length > bytes.Length - at) return fields;
                    fields.Add(((int)(key >> 3), bytes[at..(at + (int)length)]));
                    at += (int)length;
                    break;
                case 0: _ = Varint(); break;
                case 1: at += 8; break;
                case 5: at += 4; break;
                default: return fields;
            }
        }
        return fields;
    }
}
