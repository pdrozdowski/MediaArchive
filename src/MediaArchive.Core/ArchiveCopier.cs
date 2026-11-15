using System.Security.Cryptography;

namespace MediaArchive.Core;

public sealed record ArchiveCopyResult(IReadOnlyList<VerifiedArchiveCopy> VerifiedCopies, IReadOnlyList<InspectionDiagnostic> Diagnostics)
{
    public bool Succeeded => Diagnostics.Count == 0;
}

public sealed record VerifiedArchiveCopy(string InputPath, string RelativePath, string ArchivePath, string Sha256);

/// <summary>Copies inspected media into year folders without replacing existing archive files.</summary>
public sealed class ArchiveCopier
{
    private readonly Func<string, FileMode, FileAccess, FileShare, Stream> _open;

    public ArchiveCopier() : this((path, mode, access, share) => new FileStream(path, mode, access, share)) { }

    internal ArchiveCopier(Func<string, FileMode, FileAccess, FileShare, Stream> open) => _open = open;

    public ArchiveCopyResult Copy(BatchStagingResult staging, BatchInspectionResult inspection, string archiveRoot)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveRoot);

        var verified = new List<VerifiedArchiveCopy>();
        var diagnostics = new List<InspectionDiagnostic>();
        if (!staging.IsComplete)
        {
            diagnostics.Add(new("archive-staging-incomplete", "Archive was not started because one or more inputs failed staging.", null, null));
            return new(verified, diagnostics);
        }

        string root;
        ArchiveCatalog catalog;
        try
        {
            root = Path.GetFullPath(archiveRoot);
            RejectReparseComponents(root);
            Directory.CreateDirectory(root);
            RejectReparseComponents(root);
            catalog = ArchiveCatalogStore.Read(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException)
        {
            diagnostics.Add(new("archive-root-invalid", ex.Message, null, null));
            return new(verified, diagnostics);
        }

        var inputRoots = staging.Inputs.ToDictionary(input => Path.GetFullPath(input.InputPath), input => Path.GetFullPath(input.StagedRoot), PathComparer());
        var candidates = catalog.Items.Select(entry => new Candidate(entry.ArchivePath, entry.FileSize, entry.Sha256)).ToList();
        var targets = new HashSet<string>(PathComparer());
        foreach (var item in inspection.Items)
        {
            string? target = null;
            try
            {
                if (!inputRoots.TryGetValue(Path.GetFullPath(item.InputPath), out var stagedRoot))
                    throw new IOException("Inspected item does not belong to a successfully staged input.");
                var source = Path.GetFullPath(Path.Combine(stagedRoot, item.RelativePath));
                if (!IsWithin(stagedRoot, source)) throw new IOException("Inspected source path escapes its staged input.");
                RejectReparseComponents(source);
                if (!File.Exists(source)) throw new FileNotFoundException("Staged media file is missing.", source);

                using var sourceStream = _open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                var sourceSize = sourceStream.Length;
                var sourceHashBytes = SHA256.HashData(sourceStream);
                var sourceHash = Convert.ToHexString(sourceHashBytes).ToLowerInvariant();
                var matches = candidates.Where(c => c.Size == sourceSize && string.Equals(c.Sha256, sourceHash, StringComparison.OrdinalIgnoreCase)).ToArray();
                var candidateFailure = false;
                foreach (var candidate in matches)
                {
                    try
                    {
                        var candidatePath = Path.GetFullPath(Path.IsPathRooted(candidate.Path) ? candidate.Path : Path.Combine(root, candidate.Path));
                        if (!IsWithin(root, candidatePath)) throw new IOException("Duplicate candidate escapes the archive root.");
                        RejectReparseComponents(candidatePath);
                        if (!File.Exists(candidatePath)) throw new FileNotFoundException("Duplicate candidate is missing.", candidatePath);
                        using var candidateStream = _open(candidatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (candidateStream.Length != sourceSize) throw new IOException("Duplicate candidate size changed.");
                        var candidateHash = SHA256.HashData(candidateStream);
                        if (!CryptographicOperations.FixedTimeEquals(sourceHashBytes, candidateHash)) throw new IOException("Duplicate candidate content changed.");
                        target = candidatePath;
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        candidateFailure = true;
                    }
                }

                if (target is null && candidateFailure)
                    throw new IOException("Matching catalog candidate could not be freshly verified; no duplicate association was created.");

                if (target is null)
                {
                    target = Path.GetFullPath(Path.Combine(root, item.Year, Path.GetFileName(item.RelativePath)));
                    if (!IsWithin(root, target)) throw new IOException("Archive destination escapes the archive root.");
                    if (!targets.Add(target)) throw new IOException("Multiple distinct media items target the same archive path.");
                    if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Archive destination already exists.");
                    if (item.IntendedPathCollision) throw new IOException("Multiple media items target the same archive path.");
                    RejectReparseComponents(Path.GetDirectoryName(target)!);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    RejectReparseComponents(Path.GetDirectoryName(target)!);
                    using (var input = _open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = _open(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);

                    RejectReparseComponents(target);
                    using var destinationStream = _open(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var destinationHash = SHA256.HashData(destinationStream);
                    if (!CryptographicOperations.FixedTimeEquals(sourceHashBytes, destinationHash))
                        throw new IOException($"SHA-256 verification failed for archive file '{target}'.");
                }

                verified.Add(new(item.InputPath, item.RelativePath, target, sourceHash));
                candidates.Add(new(target, sourceSize, sourceHash));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                var message = target is null ? ex.Message : $"{ex.Message} Archive destination: {target}";
                diagnostics.Add(new("archive-copy-failed", message, item.InputPath, item.RelativePath));
            }
        }
        return new(verified, diagnostics);
    }

    private sealed record Candidate(string Path, long Size, string Sha256);
    private static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static void RejectReparseComponents(string path)
    {
        var full = Path.GetFullPath(path);
        var current = new DirectoryInfo(full);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Link or reparse point in path: {current.FullName}");
            current = current.Parent;
        }
        var file = new FileInfo(full);
        if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Link or reparse point in path: {file.FullName}");
    }
}
