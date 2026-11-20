using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Browser;

// Расширения, которые человек поставил из папки. Часть Sources/Search/Extensions.swift:
// папка Extensions в хранилище и в ней installed.json.
//
// Движок не копирует распакованную папку и читает её, пока расширение стоит:
// поэтому ему отдаётся копия, а не папка человека, которую тот может поправить
// или стереть. Id выдаёт движок по пути копии, поэтому копия названа не по id,
// как в апстриме, а по имени папки; installed.json связывает одно с другим.

/// Поставленное расширение. Folder — имя копии в Extensions, Source — откуда
/// она взята, Options — страница его настроек из манифеста.
public sealed record Installed(string Id, string Name, string Version, string Folder, string? Options, string? Source);

public static class Extensions
{
    public static string Root => Store.File("Extensions");
    static string List => Path.Combine(Root, "installed.json");

    /// Что о себе говорит manifest.json папки; null — это не расширение.
    public static (string Name, string Version, string? Options)? Manifest(string folder)
    {
        try
        {
            // Chrome прощает манифесту комментарии и лишние запятые.
            var read = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")), null, read) is not JsonObject manifest) return null;
            if (Text(manifest["name"]) is not { } name || Text(manifest["version"]) is not { } version) return null;
            var options = (manifest["options_ui"] is JsonObject ui ? Text(ui["page"]) : null) ?? Text(manifest["options_page"]);
            return (name, version, options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim() != "" ? text : null;

    /// Копия папки в Extensions под свободным именем. Возвращает её путь.
    public static string Stage(string source)
    {
        if (Manifest(source) is null) throw new InvalidDataException("The folder has no manifest.json with a name and a version");
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var target = Fresh(Path.GetFileName(source));
        // Папка, в которой лежит само хранилище, копировала бы себя в себя.
        if (target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The folder holds the browser's own data");
        Copy(source, target);
        return target;
    }

    /// zip расширения (из Chrome Web Store) в свободную папку Extensions. Возвращает её путь.
    public static string Unpack(byte[] zip, string name)
    {
        var target = Fresh(name);
        try
        {
            // Запись с путём наружу ExtractToDirectory не пишет, а бросает IOException.
            using (var archive = new ZipArchive(new MemoryStream(zip))) archive.ExtractToDirectory(target);
            if (Manifest(target) is null) throw new InvalidDataException("The extension has no manifest.json with a name and a version");
            return target;
        }
        catch
        {
            try { Directory.Delete(target, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// Свободный путь в Extensions для имени: без лишних знаков, вторая такая — «-2».
    static string Fresh(string name)
    {
        name = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray()).Trim('.');
        if (name == "") name = "extension";
        var target = Path.Combine(Root, name);
        for (var n = 2; Directory.Exists(target) || File.Exists(target); n++) target = Path.Combine(Root, $"{name}-{n}");
        return target;
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        // Ссылки на папки не копируем: ссылка на родителя копировала бы без конца.
        foreach (var folder in new DirectoryInfo(from).EnumerateDirectories().Where(found => !found.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            Copy(folder.FullName, Path.Combine(to, folder.Name));
    }

    public static List<Installed> All()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(List), Json.Default.ListInstalled) ?? []; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return []; }
        catch (JsonException)
        {
            Store.Quarantine(List);
            return [];
        }
    }

    public static void Keep(Installed item) => Save([.. All().Where(found => found.Id != item.Id), item]);

    /// Убрать из списка и стереть копию: движок к этому времени должен её отпустить.
    public static void Forget(string id)
    {
        var all = All();
        // Имя копии из файла, который могли править руками: только папка внутри Extensions.
        foreach (var gone in all.Where(found => found.Id == id && Path.GetFileName(found.Folder) == found.Folder && found.Folder.Trim('.') != ""))
        {
            try { Directory.Delete(Path.Combine(Root, gone.Folder), recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Save([.. all.Where(found => found.Id != id)]);
    }

    static void Save(List<Installed> all)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(all, Json.Default.ListInstalled);
        Disk.Write(List, () => data, now: true);
    }
}
