using System.Text.Json;

namespace MediaArchive.Core;

public sealed record ArchiveCatalogEntry(string InputPath, string RelativePath, string FileName, string MediaType, string Year, string ArchivePath, JsonElement? OriginalMetadata, DateTimeOffset PreparedAtUtc, DateTimeOffset ArchivedAtUtc, long FileSize, string Sha256, MediaAttributes Attributes, IReadOnlyList<string> Unknowns);
public sealed record ArchiveCatalog(int SchemaVersion, IReadOnlyList<ArchiveCatalogEntry> Items);

public static class ArchiveCatalogStore
{
    public const string FileName = "media-archive.catalog.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static ArchiveCatalog Read(string archiveRoot)
    {
        var path = Path.Combine(Path.GetFullPath(archiveRoot), FileName);
        string json;
        try { json = File.ReadAllText(path); }
        catch (FileNotFoundException) { return new(1, []); }
        catch (DirectoryNotFoundException) { return new(1, []); }
        var catalog = JsonSerializer.Deserialize<ArchiveCatalog>(json);
        if (catalog is null || catalog.SchemaVersion != 1 || catalog.Items is null ||
            catalog.Items.Any(item => item is null || item.InputPath is null || item.RelativePath is null ||
                item.FileName is null || item.MediaType is null || item.Year is null || item.ArchivePath is null ||
                item.Sha256 is null || item.Attributes is null || item.Unknowns is null || item.Unknowns.Any(value => value is null)))
            throw new InvalidDataException("Catalog is malformed or has an unsupported schema version.");
        return catalog;
    }

    public static void Save(string archiveRoot, IReadOnlyList<ArchiveCatalogEntry> additions)
    {
        var root = Path.GetFullPath(archiveRoot);
        Directory.CreateDirectory(root);
        var existing = Read(root);
        var updated = new ArchiveCatalog(1, existing.Items.Concat(additions).ToArray());
        var target = Path.Combine(root, FileName);
        var temporary = Path.Combine(root, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, updated, Options);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
