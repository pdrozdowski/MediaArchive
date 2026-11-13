using System.IO.Compression;

namespace MediaArchive.Core;

public sealed record BatchInputResult(string InputPath, string StagedRoot, bool Succeeded, string? Error);

public sealed record BatchStagingResult(string StagingRoot, IReadOnlyList<BatchInputResult> Inputs)
{
    public bool IsComplete => Inputs.All(input => input.Succeeded);
}

/// <summary>Copies ZIP and directory inputs to isolated roots without modifying their sources.</summary>
public sealed class BatchStager
{
    private readonly Action<string, string>? _beforeFileWrite;

    public BatchStager() { }

    internal BatchStager(Action<string, string> beforeFileWrite) => _beforeFileWrite = beforeFileWrite;

    public BatchStagingResult Stage(IEnumerable<string> inputs, string stagingParent, string archiveRoot, string stagingDirectoryName)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var supplied = inputs.ToArray();
        try { return StageCore(supplied, stagingParent, archiveRoot, stagingDirectoryName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            var root = SafeFullPath(stagingParent, stagingDirectoryName);
            var failures = supplied.Select(input => new BatchInputResult(input ?? "", "", false, ex.Message)).ToArray();
            if (failures.Length == 0) failures = [new("", "", false, ex.Message)];
            return new(root, failures);
        }
    }

    private static string SafeFullPath(string parent, string name)
    {
        try { return Path.GetFullPath(Path.Combine(parent, name)); }
        catch { return ""; }
    }

    private BatchStagingResult StageCore(IEnumerable<string> inputs, string stagingParent, string archiveRoot, string stagingDirectoryName)
    {
        var sourcePaths = inputs.Select(Path.GetFullPath).ToArray();
        if (sourcePaths.Length == 0) throw new ArgumentException("At least one input is required.", nameof(inputs));
        var parent = Path.GetFullPath(stagingParent);
        var archive = Path.GetFullPath(archiveRoot);
        if (string.IsNullOrWhiteSpace(stagingDirectoryName) || Path.IsPathRooted(stagingDirectoryName) || stagingDirectoryName is "." or ".." || stagingDirectoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A simple staging directory name is required.", nameof(stagingDirectoryName));
        var staging = Path.Combine(parent, stagingDirectoryName);

        foreach (var path in sourcePaths)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) throw new DirectoryNotFoundException($"Path does not exist: {path}");
            RejectReparseComponents(path);
        }
        RejectReparseComponents(parent);
        RejectReparseComponents(archive);
        RejectReparseComponents(staging);
        var allRoots = sourcePaths.Append(archive).Append(staging).ToArray();
        for (var i = 0; i < allRoots.Length; i++)
            for (var j = i + 1; j < allRoots.Length; j++)
                if (Overlaps(allRoots[i], allRoots[j])) throw new IOException($"Input and destination paths overlap: {allRoots[i]} and {allRoots[j]}");
        if (Directory.Exists(staging) || File.Exists(staging)) throw new IOException($"Staging directory already exists: {staging}");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(staging);

        var results = new List<BatchInputResult>();
        for (var index = 0; index < sourcePaths.Length; index++)
        {
            var source = sourcePaths[index];
            var destination = Path.Combine(staging, $"input-{index + 1:D4}");
            try
            {
                Directory.CreateDirectory(destination);
                if (Directory.Exists(source)) CopyDirectory(source, destination);
                else if (string.Equals(Path.GetExtension(source), ".zip", StringComparison.OrdinalIgnoreCase)) ExtractZip(source, destination);
                else throw new IOException("Input must be a ZIP file or extracted folder.");
                results.Add(new(source, destination, true, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                results.Add(new(source, destination, false, ex.Message));
            }
        }
        return new(staging, results);
    }

    private void CopyDirectory(string source, string destination)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Link or reparse point rejected: {entry}");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                CopyDirectory(entry, target);
            }
            else
            {
                _beforeFileWrite?.Invoke(entry, target);
                File.Copy(entry, target, overwrite: false);
            }
        }
    }

    private void ExtractZip(string source, string destination)
    {
        using var archive = ZipFile.OpenRead(source);
        var destinations = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || Path.IsPathRooted(name) || name.Split('/').Any(part => part == ".." || part.Contains(':')))
                throw new IOException($"Unsafe ZIP path rejected: {entry.FullName}");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new IOException($"ZIP link rejected: {entry.FullName}");
            var target = Path.GetFullPath(Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(destination, target)) throw new IOException($"Unsafe ZIP path rejected: {entry.FullName}");
            if (!destinations.Add(target)) throw new IOException($"Duplicate ZIP destination rejected: {entry.FullName}");
            var ancestor = Path.GetDirectoryName(target);
            while (ancestor is not null && IsWithin(destination, ancestor) && ancestor != destination)
            {
                if (File.Exists(ancestor)) throw new IOException($"ZIP file/directory collision rejected: {entry.FullName}");
                ancestor = Path.GetDirectoryName(ancestor);
            }
            var isDirectory = name.EndsWith('/') || string.IsNullOrEmpty(entry.Name);
            if (isDirectory)
            {
                if (File.Exists(target)) throw new IOException($"ZIP file/directory collision rejected: {entry.FullName}");
                Directory.CreateDirectory(target);
                continue;
            }
            if (Directory.Exists(target)) throw new IOException($"ZIP file/directory collision rejected: {entry.FullName}");
            var directory = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(directory);
            RejectReparseComponents(directory);
            _beforeFileWrite?.Invoke(source, target);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
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

    private static bool Overlaps(string left, string right) => IsWithin(left, right) || IsWithin(right, left);
    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}
