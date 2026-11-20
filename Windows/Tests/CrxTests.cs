using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Browser.Tests;

// .crx из Chrome Web Store: свои, подписанные ключом, созданным здесь же, —
// без сети и без чужих файлов.
static class CrxTests
{
    public static void Run()
    {
        const string some = "bcjindcccaagfpapjjmafapmmgkkhgoa";
        Check.Expect(Crx.Id(some) == some, "голый id");
        Check.Expect(Crx.Id($"https://chromewebstore.google.com/detail/json-formatter/{some}?hl=en") == some, "id из ссылки магазина");
        Check.Expect(Crx.Id($"https://chrome.google.com/webstore/detail/{some.ToUpperInvariant()}") == some, "id из старой ссылки, буквы любого регистра");
        Check.Expect(Crx.Id("https://chromewebstore.google.com/") is null && Crx.Id(some + "a") is null && Crx.Id(some[..31]) is null,
            "не id: адрес без id, 33 буквы подряд, 31 буква");
        var url = Crx.DownloadUrl(some, "142.0.3595.53").ToString();
        Check.Expect(url.StartsWith("https://clients2.google.com/service/update2/crx?") && url.Contains("prodversion=142.0.3595.53")
            && url.Contains($"x=id%3D{some}%26installsource%3Dondemand%26uc"), "адрес загрузки: магазин, версия, id");

        using var owner = RSA.Create(2048);
        using var stranger = RSA.Create(2048);
        var spki = owner.ExportSubjectPublicKeyInfo();
        var id = Crx.Letters(SHA256.HashData(spki).AsSpan(0, 16));
        var zip = Zip(("manifest.json", """{ "name": "Signed", "version": "3.1" }"""), ("js/a.js", "1"));
        var good = Pack(owner, spki, id, zip);

        Check.Expect(Refuses(() => Crx.VerifiedZip(good, id)) is null && Crx.VerifiedZip(good, id).SequenceEqual(zip),
            "подписанный владельцем .crx отдаёт свой zip");
        var tampered = (byte[])good.Clone();
        tampered[^10] ^= 1;
        Check.Expect(Refuses(() => Crx.VerifiedZip(tampered, id)) == "The extension's signature doesn't hold up", "изменённый по дороге zip не проходит");
        Check.Expect(Refuses(() => Crx.VerifiedZip(good, some)) is not null, "чужой id не проходит");
        Check.Expect(Refuses(() => Crx.VerifiedZip(Pack(stranger, spki, id, zip), id)) is not null,
            "подпись чужим ключом под ключом владельца не проходит");
        Check.Expect(Refuses(() => Crx.VerifiedZip(Pack(stranger, stranger.ExportSubjectPublicKeyInfo(), id, zip), id)) is not null,
            "ключ, который не даёт этот id, не проходит");
        var crx2 = (byte[])good.Clone();
        crx2[4] = 2;
        var cut = good[..20];
        Check.Expect(Refuses(() => Crx.VerifiedZip(zip, id)) == "What came back isn't a Chrome extension"
            && Refuses(() => Crx.VerifiedZip(crx2, id)) is not null && Refuses(() => Crx.VerifiedZip(cut, id)) is not null,
            "не .crx, CRX2 и обрезанный файл не проходят");

        Check.World("crx");
        var folder = Extensions.Unpack(Crx.VerifiedZip(good, id), id);
        Check.Expect(Path.GetFileName(folder) == id && Extensions.Manifest(folder) == ("Signed", "3.1", null)
            && File.Exists(Path.Combine(folder, "js", "a.js")), "zip распакован в Extensions под id");
        var escape = Zip(("manifest.json", """{ "name": "Evil", "version": "1" }"""), ("../outside.txt", "x"));
        Check.Expect(Refuses(() => Extensions.Unpack(escape, "evil")) is not null && !File.Exists(Path.Combine(Store.Folder, "outside.txt"))
            && !Directory.Exists(Path.Combine(Extensions.Root, "evil")), "запись с путём наружу не пишется, папка не остаётся");
        Check.Expect(Refuses(() => Extensions.Unpack(Zip(("readme.txt", "x")), "bare")) is not null
            && !Directory.Exists(Path.Combine(Extensions.Root, "bare")), "zip без манифеста не остаётся в Extensions");
    }

    /// Текст ошибки разбора или null, если ошибки не было.
    static string? Refuses(Action act)
    {
        try { act(); return null; }
        catch (Exception e) when (e is InvalidDataException or IOException) { return e.Message; }
    }

    static byte[] Zip(params (string Name, string Text)[] files)
    {
        var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var entry = archive.CreateEntry(name).Open()) entry.Write(Encoding.UTF8.GetBytes(text));
        return memory.ToArray();
    }

    /// CRX3, как его пишет магазин: заголовок с доказательством и подписанным id, потом zip.
    static byte[] Pack(RSA signer, byte[] spki, string id, byte[] zip)
    {
        var raw = Convert.FromHexString(string.Concat(id.Select(c => "0123456789abcdef"[c - 'a'])));
        var signed = Field(1, raw);
        var message = Concat("CRX3 SignedData\0"u8.ToArray(), Le(signed.Length), signed, zip);
        var signature = signer.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var header = Concat(Field(2, Concat(Field(1, spki), Field(2, signature))), Field(10000, signed));
        return Concat("Cr24"u8.ToArray(), Le(3), Le(header.Length), header, zip);
    }

    static byte[] Field(int number, byte[] value) => Concat(Varint((number << 3) | 2), Varint(value.Length), value);

    static byte[] Varint(int value)
    {
        var bytes = new List<byte>();
        for (; value >= 0x80; value >>= 7) bytes.Add((byte)(value | 0x80));
        bytes.Add((byte)value);
        return [.. bytes];
    }

    static byte[] Le(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];
}
