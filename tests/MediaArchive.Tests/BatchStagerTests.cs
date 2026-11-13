using System.IO.Compression;
using MediaArchive.Core;

namespace MediaArchive.Tests;

public sealed class BatchStagerTests
{
    [Fact]
    public void StagesZipAndFolderIntoSeparateRootsAndPreservesHierarchyAndSources()
    {
        using var temp = new TemporaryDirectory();
        var sourceFolder = Directory.CreateDirectory(Path.Combine(temp.Path, "folder")).FullName;
        Directory.CreateDirectory(Path.Combine(sourceFolder, "nested"));
        File.WriteAllText(Path.Combine(sourceFolder, "nested", "photo.jpg"), "folder bytes");
        var zip = Path.Combine(temp.Path, "part.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("Takeout/Photos/video.mp4");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("zip bytes");
        }
        var originalZip = File.ReadAllBytes(zip);

        var result = new BatchStager().Stage([zip, sourceFolder], Path.Combine(temp.Path, "stage-parent"), Path.Combine(temp.Path, "archive"), "run-1");

        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Inputs.Count);
        Assert.Equal("zip bytes", File.ReadAllText(Path.Combine(result.Inputs[0].StagedRoot, "Takeout", "Photos", "video.mp4")));
        Assert.Equal("folder bytes", File.ReadAllText(Path.Combine(result.Inputs[1].StagedRoot, "nested", "photo.jpg")));
        Assert.Equal(originalZip, File.ReadAllBytes(zip));
        Assert.Equal("folder bytes", File.ReadAllText(Path.Combine(sourceFolder, "nested", "photo.jpg")));
        Assert.NotEqual(result.Inputs[0].StagedRoot, result.Inputs[1].StagedRoot);
    }

    [Fact]
    public void UnsafeZipTraversalReturnsIncompleteAndLeavesSourceUntouched()
    {
        using var temp = new TemporaryDirectory();
        var zip = Path.Combine(temp.Path, "unsafe.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open());
            writer.Write("unsafe");
        }
        var original = File.ReadAllBytes(zip);

        var result = new BatchStager().Stage([zip], Path.Combine(temp.Path, "stage-parent"), Path.Combine(temp.Path, "archive"), "run-1");

        Assert.False(result.IsComplete);
        Assert.False(result.Inputs[0].Succeeded);
        Assert.Contains("Unsafe ZIP path", result.Inputs[0].Error);
        Assert.False(File.Exists(Path.Combine(temp.Path, "escape.txt")));
        Assert.Equal(original, File.ReadAllBytes(zip));
    }

    [Fact]
    public void OverlappingSourceAndArchiveReturnsDiagnosticWithoutCreatingStaging()
    {
        using var temp = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var parent = Path.Combine(temp.Path, "stage-parent");

        var result = new BatchStager().Stage([source], parent, source, "run-1");

        Assert.False(result.IsComplete);
        Assert.Contains("overlap", result.Inputs[0].Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(parent));
    }

    [Fact]
    public void ExistingStagingDirectoryReturnsDiagnosticAndIsNotOverwritten()
    {
        using var temp = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var parent = Directory.CreateDirectory(Path.Combine(temp.Path, "stage-parent")).FullName;
        var staging = Directory.CreateDirectory(Path.Combine(parent, "run-1")).FullName;
        File.WriteAllText(Path.Combine(staging, "keep.txt"), "keep");

        var result = new BatchStager().Stage([source], parent, Path.Combine(temp.Path, "archive"), "run-1");

        Assert.False(result.IsComplete);
        Assert.Contains("already exists", result.Inputs[0].Error);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(staging, "keep.txt")));
    }

    [Fact]
    public void MalformedZipReturnsIncompleteAndKeepsOriginalBytes()
    {
        using var temp = new TemporaryDirectory();
        var zip = Path.Combine(temp.Path, "broken.zip");
        File.WriteAllText(zip, "not a zip");
        var original = File.ReadAllBytes(zip);

        var result = new BatchStager().Stage([zip], Path.Combine(temp.Path, "stage-parent"), Path.Combine(temp.Path, "archive"), "run-1");

        Assert.False(result.IsComplete);
        Assert.False(result.Inputs[0].Succeeded);
        Assert.Equal(original, File.ReadAllBytes(zip));
    }

    [Fact]
    public void RejectsDuplicateZipDestination()
    {
        using var temp = new TemporaryDirectory();
        var zip = Path.Combine(temp.Path, "duplicate.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var contents in new[] { "one", "two" })
            {
                using var writer = new StreamWriter(archive.CreateEntry("same.txt").Open());
                writer.Write(contents);
            }
        }
        var original = File.ReadAllBytes(zip);

        var result = new BatchStager().Stage([zip], Path.Combine(temp.Path, "stage-parent"), Path.Combine(temp.Path, "archive"), "run-1");

        Assert.False(result.IsComplete);
        Assert.Contains("Duplicate ZIP destination", result.Inputs[0].Error);
        Assert.Equal(original, File.ReadAllBytes(zip));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsZipFileDirectoryHierarchyCollision(bool fileFirst)
    {
        using var temp = new TemporaryDirectory();
        var zip = Path.Combine(temp.Path, "collision.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var names = fileFirst ? new[] { "node", "node/child.txt" } : new[] { "node/child.txt", "node" };
            foreach (var name in names)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("x");
            }
        }

        var result = new BatchStager().Stage([zip], Path.Combine(temp.Path, "stage-parent"), Path.Combine(temp.Path, "archive"), "run-1");

        Assert.False(result.IsComplete);
        Assert.Contains("collision", result.Inputs[0].Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsUnixSymlinkFlagInZip()
    {
        using var temp = new TemporaryDirectory();
        var zip = Path.Combine(temp.Path, "link.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var link = archive.CreateEntry("link.txt");
            link.ExternalAttributes = unchecked((int)0xA1FF0000); // Unix S_IFLNK plus mode bits
            using var writer = new StreamWriter(link.Open());
            writer.Write("target");
        }

        var result = new BatchStager().Stage([zip], Path.Combine(temp.Path, "stage-parent"), Path.Combine(temp.Path, "archive"), "run-1");

        Assert.False(result.IsComplete);
        Assert.Contains("link rejected", result.Inputs[0].Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsOverlappingSourceRootsBeforeCreatingStaging()
    {
        using var temp = new TemporaryDirectory();
        var parent = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(parent, "child")).FullName;
        var stageParent = Path.Combine(temp.Path, "stage-parent");

        var result = new BatchStager().Stage([parent, child], stageParent, Path.Combine(temp.Path, "archive"), "run-1");

        Assert.False(result.IsComplete);
        Assert.Contains("overlap", result.Inputs[0].Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(stageParent));
    }

    [Fact]
    public void RejectsStagingParentSymlinkAliasingSourceBeforeCreatingAnyOutput()
    {
        using var temp = new TemporaryDirectory();
        var real = Directory.CreateDirectory(Path.Combine(temp.Path, "real")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(real, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "keep.txt"), "source bytes");
        var alias = Path.Combine(temp.Path, "alias");
        try { Directory.CreateSymbolicLink(alias, real); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Directory symlinks are unavailable: {ex.Message}");
        }

        var result = new BatchStager().Stage([source], alias, Path.Combine(temp.Path, "archive"), "run-1");

        Assert.False(result.IsComplete);
        Assert.Contains("reparse point", result.Inputs[0].Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(real, "run-1")));
        Assert.Equal("source bytes", File.ReadAllText(Path.Combine(source, "keep.txt")));
    }

    [Fact]
    public void InjectedFileWriteFailureMarksInputIncompleteAndPreservesSource()
    {
        using var temp = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var sourceFile = Path.Combine(source, "photo.jpg");
        File.WriteAllText(sourceFile, "original bytes");
        var stager = new BatchStager((_, _) => throw new IOException("injected write failure"));

        var result = stager.Stage([source], Path.Combine(temp.Path, "stage-parent"), Path.Combine(temp.Path, "archive"), "run-1");

        Assert.False(result.IsComplete);
        Assert.False(result.Inputs[0].Succeeded);
        Assert.Contains("injected write failure", result.Inputs[0].Error);
        Assert.Equal("original bytes", File.ReadAllText(sourceFile));
        Assert.False(File.Exists(Path.Combine(result.Inputs[0].StagedRoot, "photo.jpg")));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
