using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaArchive.Core;

public sealed record InspectionDiagnostic(string Code, string Message, string? InputPath, string? RelativePath);

public sealed record InspectedMediaItem(
    string InputPath,
    string RelativePath,
    string MediaKind,
    JsonElement? Sidecar,
    IReadOnlyList<string> Unknowns,
    string Year,
    string IntendedArchivePath,
    DateTimeOffset PreparedAtUtc,
    bool IntendedPathCollision);

public sealed record BatchInspectionResult(
    IReadOnlyList<InspectedMediaItem> Items,
    IReadOnlyList<string> UnrecognizedFiles,
    IReadOnlyList<InspectionDiagnostic> Diagnostics,
    DateTimeOffset PreparedAtUtc);

/// <summary>Prepares a read-only inspection view of successfully staged batch inputs.</summary>
public sealed class BatchInspector
{
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".bmp", ".tif", ".tiff" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mov", ".m4v", ".avi", ".mpg", ".mpeg", ".3gp", ".webm" };
    private static readonly Regex DateToken = new(@"(?<!\d)(\d{8}|\d{4}-\d{2}-\d{2})(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public BatchInspectionResult Inspect(BatchStagingResult staging, string archiveRoot)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveRoot);

        var preparedAt = DateTimeOffset.UtcNow;
        var diagnostics = new List<InspectionDiagnostic>();
        foreach (var failed in staging.Inputs.Where(input => !input.Succeeded))
            diagnostics.Add(new("staging-failed", failed.Error ?? "Input staging failed.", failed.InputPath, null));

        var roots = staging.Inputs.Where(input => input.Succeeded)
            .Select(input => new InputRoot(input.InputPath, Path.GetFullPath(input.StagedRoot)))
            .ToArray();
        var media = new List<MediaFile>();
        var unrecognized = new List<string>();
        var sidecars = new List<SidecarFile>();

        foreach (var root in roots)
        {
            foreach (var file in Directory.EnumerateFiles(root.StagedRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root.StagedRoot, file);
                var extension = Path.GetExtension(file);
                if (PhotoExtensions.Contains(extension) || VideoExtensions.Contains(extension))
                {
                    media.Add(new(root, file, relative, Path.GetFileName(file),
                        PhotoExtensions.Contains(extension) ? "photo" : "video"));
                }
                else if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
                {
                    sidecars.Add(ParseSidecar(root, file, relative, diagnostics));
                }
                else
                {
                    unrecognized.Add($"{root.InputPath}:{relative}");
                }
            }
        }

        var parsedSidecars = sidecars.Where(sidecar => sidecar.Title is not null).ToArray();
        var claims = media.GroupBy(item => (item.Root.StagedRoot, Directory: Path.GetDirectoryName(item.Path)!, item.Basename))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var matchedSidecars = new HashSet<SidecarFile>();
        var records = new List<InspectedMediaItem>();

        foreach (var item in media)
        {
            var directory = Path.GetDirectoryName(item.Path)!;
            var local = parsedSidecars.Where(sidecar => sidecar.Root.StagedRoot == item.Root.StagedRoot
                && Path.GetDirectoryName(sidecar.Path) == directory
                && string.Equals(sidecar.Title, item.Basename, StringComparison.Ordinal)).ToArray();
            var titleClaims = claims.TryGetValue((item.Root.StagedRoot, directory, item.Basename), out var claimGroup) ? claimGroup.Length : 0;
            SidecarFile? associated = null;
            if (local.Length == 1 && titleClaims == 1)
            {
                associated = local[0];
                matchedSidecars.Add(associated);
            }
            else if (local.Length > 1 || titleClaims > 1)
            {
                diagnostics.Add(new("sidecar-competing", $"Metadata association is ambiguous for '{item.Basename}'.", item.Root.InputPath, item.RelativePath));
            }
            else
            {
                var crossPart = parsedSidecars.Any(sidecar => sidecar.Root.StagedRoot != item.Root.StagedRoot
                    && string.Equals(sidecar.Title, item.Basename, StringComparison.Ordinal));
                diagnostics.Add(new(crossPart ? "sidecar-cross-part" : "sidecar-missing",
                    crossPart ? $"Matching metadata exists in a different input part for '{item.Basename}'." : $"No unambiguous same-directory metadata for '{item.Basename}'.",
                    item.Root.InputPath, item.RelativePath));
            }

            var year = SelectYear(associated?.Document, Path.GetFileNameWithoutExtension(item.Path), diagnostics, item.Root.InputPath, item.RelativePath);
            var unknowns = new List<string>();
            if (associated is null) unknowns.Add("sidecar");
            if (year == "Unknown") unknowns.Add("year");
            var intendedPath = Path.GetFullPath(Path.Combine(archiveRoot, year, Path.GetFileName(item.Path)));
            records.Add(new(item.Root.InputPath, item.RelativePath, item.Kind, associated?.Document, unknowns,
                year, intendedPath, preparedAt, false));
        }

        foreach (var sidecar in parsedSidecars.Where(sidecar => !matchedSidecars.Contains(sidecar)))
        {
            var anotherPart = media.Any(item => item.Root.StagedRoot != sidecar.Root.StagedRoot
                && string.Equals(item.Basename, sidecar.Title, StringComparison.Ordinal));
            diagnostics.Add(new(anotherPart ? "sidecar-cross-part-unresolved" : "sidecar-unresolved",
                anotherPart ? $"Sidecar title '{sidecar.Title}' refers to media in another input part." : $"Sidecar '{sidecar.RelativePath}' has no unique same-directory media match.",
                sidecar.Root.InputPath, sidecar.RelativePath));
        }

        var colliding = records.GroupBy(record => record.IntendedArchivePath,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Where(group => group.Count() > 1).SelectMany(group => group).ToHashSet();
        foreach (var record in colliding)
            diagnostics.Add(new("intended-path-collision", $"Multiple items target '{record.IntendedArchivePath}'.", record.InputPath, record.RelativePath));
        var finalRecords = records.Select(record => record with { IntendedPathCollision = colliding.Contains(record) }).ToArray();
        return new(finalRecords, unrecognized, diagnostics, preparedAt);
    }

    private static SidecarFile ParseSidecar(InputRoot root, string path, string relative, List<InspectionDiagnostic> diagnostics)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var title = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("title", out var titleValue)
                && titleValue.ValueKind == JsonValueKind.String ? titleValue.GetString() : null;
            if (string.IsNullOrEmpty(title))
                diagnostics.Add(new("sidecar-unresolved", "Sidecar has no usable string title.", root.InputPath, relative));
            return new(root, path, relative, title, document.RootElement.Clone());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new("sidecar-malformed", $"Could not read sidecar JSON: {ex.Message}", root.InputPath, relative));
            return new(root, path, relative, null, null);
        }
    }

    private static string SelectYear(JsonElement? sidecar, string filename, List<InspectionDiagnostic> diagnostics, string inputPath, string relative)
    {
        if (sidecar.HasValue && sidecar.Value.TryGetProperty("photoTakenTime", out var photoTaken)
            && photoTaken.ValueKind == JsonValueKind.Object && photoTaken.TryGetProperty("timestamp", out var timestamp))
        {
            var raw = timestamp.ValueKind == JsonValueKind.String ? timestamp.GetString() : timestamp.ToString();
            if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds) && seconds != 0)
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.Year.ToString(CultureInfo.InvariantCulture); }
                catch (ArgumentOutOfRangeException) { }
            }
            diagnostics.Add(new("date-metadata-invalid", "Photo taken time is missing, zero, malformed, or out of range; checking filename.", inputPath, relative));
        }

        var tokens = DateToken.Matches(filename).Select(match => match.Groups[1].Value).ToArray();
        if (tokens.Length > 0)
        {
            var dates = new List<DateOnly>();
            foreach (var token in tokens)
            {
                var format = token.Length == 8 ? "yyyyMMdd" : "yyyy-MM-dd";
                if (!DateOnly.TryParseExact(token, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    diagnostics.Add(new("date-filename-invalid", $"Filename contains invalid date token '{token}'.", inputPath, relative));
                    return "Unknown";
                }
                dates.Add(date);
            }
            var distinct = dates.Distinct().ToArray();
            if (distinct.Length == 1) return distinct[0].Year.ToString(CultureInfo.InvariantCulture);
            diagnostics.Add(new("date-filename-ambiguous", "Filename contains multiple distinct dates.", inputPath, relative));
        }
        return "Unknown";
    }

    private sealed record InputRoot(string InputPath, string StagedRoot);
    private sealed record MediaFile(InputRoot Root, string Path, string RelativePath, string Basename, string Kind);
    private sealed record SidecarFile(InputRoot Root, string Path, string RelativePath, string? Title, JsonElement? Document);
}
