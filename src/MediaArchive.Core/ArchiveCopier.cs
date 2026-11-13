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

        var copied = new List<VerifiedArchiveCopy>();
        var diagnostics = new List<InspectionDiagnostic>();
        if (!staging.IsComplete)
        {
            diagnostics.Add(new("archive-staging-incomplete", "Archive was not started because one or more inputs failed staging.", null, null));
            return new(copied, diagnostics);
        }

        string root;
        try
        {
            root = Path.GetFullPath(archiveRoot);
            RejectReparseComponents(root);
            Directory.CreateDirectory(root);
            RejectReparseComponents(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new("archive-root-invalid", ex.Message, null, null));
            return new(copied, diagnostics);
        }

        var inputRoots = staging.Inputs.ToDictionary(input => Path.GetFullPath(input.InputPath), input => Path.GetFullPath(input.StagedRoot), PathComparer());
        var targets = new HashSet<string>(PathComparer());
        foreach (var item in inspection.Items)
        {
            string? inputPath = item.InputPath;
            string? target = null;
            try
            {
                if (!inputRoots.TryGetValue(Path.GetFullPath(item.InputPath), out var stagedRoot))
                    throw new IOException("Inspected item does not belong to a successfully staged input.");
                var source = Path.GetFullPath(Path.Combine(stagedRoot, item.RelativePath));
                if (!IsWithin(stagedRoot, source)) throw new IOException("Inspected source path escapes its staged input.");
                RejectReparseComponents(source);
                if (!File.Exists(source)) throw new FileNotFoundException("Staged media file is missing.", source);

                target = Path.GetFullPath(Path.Combine(root, item.Year, Path.GetFileName(item.RelativePath)));
                if (!IsWithin(root, target)) throw new IOException("Archive destination escapes the archive root.");
                if (item.IntendedPathCollision || !targets.Add(target)) throw new IOException("Multiple media items target the same archive path.");
                RejectReparseComponents(Path.GetDirectoryName(target)!);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                RejectReparseComponents(Path.GetDirectoryName(target)!);
                using (var input = _open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = _open(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);

                RejectReparseComponents(target);
                using var sourceStream = _open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var destinationStream = _open(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                var sourceHash = SHA256.HashData(sourceStream);
                var destinationHash = SHA256.HashData(destinationStream);
                if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
                    throw new IOException($"SHA-256 verification failed for archive file '{target}'.");
                copied.Add(new(item.InputPath, item.RelativePath, target, Convert.ToHexString(destinationHash).ToLowerInvariant()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                var message = target is null ? ex.Message : $"{ex.Message} Archive destination: {target}";
                diagnostics.Add(new("archive-copy-failed", message, inputPath, item.RelativePath));
            }
        }
        return new(copied, diagnostics);
    }

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
