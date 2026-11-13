namespace MediaArchive.Core;

public sealed record CatalogIntegrationResult(ArchiveCatalog Catalog, IReadOnlyList<InspectionDiagnostic> Diagnostics, IReadOnlyList<InspectionDiagnostic> Warnings);

public sealed class ArchiveCatalogIntegrator
{
    private readonly MediaProbe _probe;
    private readonly Action<string, IReadOnlyList<ArchiveCatalogEntry>> _save;
    public ArchiveCatalogIntegrator(MediaProbe? probe = null, Action<string, IReadOnlyList<ArchiveCatalogEntry>>? save = null)
    {
        _probe = probe ?? new MediaProbe();
        _save = save ?? ArchiveCatalogStore.Save;
    }

    public CatalogIntegrationResult Integrate(BatchStagingResult staging, BatchInspectionResult inspection, ArchiveCopyResult copies, string archiveRoot)
    {
        var warnings = new List<InspectionDiagnostic>();
        var failures = new List<InspectionDiagnostic>();
        ArchiveCatalog existing;
        try { existing = ArchiveCatalogStore.Read(archiveRoot); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            failures.Add(new("catalog-read-failed", ex.Message, null, null));
            foreach (var copy in copies.VerifiedCopies)
                failures.Add(new("catalog-read-failed", $"Verified copy remains uncataloged: {copy.ArchivePath}", copy.InputPath, copy.RelativePath));
            return new(new(1, []), failures, warnings);
        }
        var inputRoots = staging.Inputs.ToDictionary(i => Path.GetFullPath(i.InputPath), i => Path.GetFullPath(i.StagedRoot), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var records = new List<ArchiveCatalogEntry>();
        foreach (var copy in copies.VerifiedCopies)
        {
            try
            {
                var inspected = inspection.Items.Single(i => i.InputPath == copy.InputPath && i.RelativePath == copy.RelativePath);
                var source = Path.GetFullPath(Path.Combine(inputRoots[Path.GetFullPath(copy.InputPath)], copy.RelativePath));
                var probe = _probe.Probe(source, inspected.MediaKind);
                foreach (var message in probe.Diagnostics) warnings.Add(new("media-attribute-unknown", message, copy.InputPath, copy.RelativePath));
                var attributes = probe.Attributes;
                var unknowns = inspected.Unknowns.Concat(attributes.Unknowns(inspected.MediaKind)).Distinct().ToList();
                records.Add(new(copy.InputPath, copy.RelativePath, Path.GetFileName(copy.RelativePath), inspected.MediaKind, inspected.Year, copy.ArchivePath, inspected.Sidecar?.Clone(), inspected.PreparedAtUtc, DateTimeOffset.UtcNow, new FileInfo(source).Length, copy.Sha256, attributes, unknowns));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                failures.Add(new("catalog-item-failed", $"Verified copy remains uncataloged: {ex.Message}", copy.InputPath, copy.RelativePath));
            }
        }

        if (records.Count > 0)
        {
            try { _save(archiveRoot, records); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                foreach (var record in records) failures.Add(new("catalog-write-failed", $"Verified copy remains uncataloged: {ex.Message}", record.InputPath, record.RelativePath));
            }
        }
        ArchiveCatalog catalog;
        try { catalog = ArchiveCatalogStore.Read(archiveRoot); }
        catch { catalog = new(1, existing.Items); }
        return new(catalog, failures, warnings);
    }
}
