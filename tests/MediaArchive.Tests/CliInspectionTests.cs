using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using MediaArchive.Core;

namespace MediaArchive.Tests;

public sealed class CliInspectionTests
{
    [Fact]
    public void BrowseReportsStructurallyMalformedCatalogWithoutCrashing()
    {
        using var temp = new TemporaryDirectory();
        var catalog = Path.Combine(temp.Path, ArchiveCatalogStore.FileName);
        const string malformed = "{\"SchemaVersion\":1,\"Items\":[null]}";
        File.WriteAllText(catalog, malformed);

        var (exitCode, _, stderr) = RunWithConsole(["browse", "--archive-root", temp.Path]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Catalog: failed", stderr);
        Assert.Equal(malformed, File.ReadAllText(catalog));
    }

    [Fact]
    public void InspectCommandStagesMixedBatchAndDisplaysRecordsAndDiagnostics()
    {
        using var temp = new TemporaryDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "folder-input")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "folder-20200102.jpg"), [1, 2]);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "unrecognized");
        var zip = Path.Combine(temp.Path, "part.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var photo = new StreamWriter(archive.CreateEntry("Takeout/zip-photo.jpg").Open())) photo.Write("photo");
            using (var metadata = new StreamWriter(archive.CreateEntry("Takeout/zip-photo.json").Open()))
                metadata.Write("{\"title\":\"zip-photo.jpg\",\"photoTakenTime\":{\"timestamp\":\"1609459200\"},\"note\":\"kept\"}");
        }

        var (exitCode, stdout, stderr) = RunWithConsole([
            "inspect", "--input", zip, "--input", folder,
            "--staging-parent", Path.Combine(temp.Path, "stage-parent"), "--staging-name", "run-1",
            "--archive-root", Path.Combine(temp.Path, "archive")]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Batch: complete", stdout);
        Assert.Contains("year=2021", stdout);
        Assert.Contains("year=2020", stdout);
        Assert.Contains("metadata={\"title\":\"zip-photo.jpg\"", stdout);
        Assert.Contains("Unrecognized:", stdout);
        Assert.Contains("sidecar-missing", stderr);
        Assert.True(File.Exists(zip));
        Assert.True(File.Exists(Path.Combine(folder, "folder-20200102.jpg")));
    }

    [Fact]
    public void InspectCommandReturnsNonzeroAndReportsStagingFailure()
    {
        using var temp = new TemporaryDirectory();
        var brokenZip = Path.Combine(temp.Path, "broken.zip");
        File.WriteAllText(brokenZip, "not a zip");

        var (exitCode, stdout, stderr) = RunWithConsole([
            "inspect", "--input", brokenZip,
            "--staging-parent", Path.Combine(temp.Path, "stage-parent"), "--staging-name", "run-1",
            "--archive-root", Path.Combine(temp.Path, "archive")]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Batch: incomplete", stdout);
        Assert.Contains("Input: failed", stdout);
        Assert.Contains("staging-failed", stderr);
        Assert.Equal("not a zip", File.ReadAllText(brokenZip));
    }

    [Fact]
    public void ArchiveCommandCopiesMediaToYearFoldersAndDoesNotOverwrite()
    {
        using var temp = new TemporaryDirectory();
        var input = Directory.CreateDirectory(Path.Combine(temp.Path, "input")).FullName;
        var photo = Path.Combine(input, "photo-20200102.jpg");
        File.WriteAllBytes(photo, [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(input, "unknown.jpg"), [4]);
        var archive = Path.Combine(temp.Path, "archive");

        var (exitCode, stdout, stderr) = RunWithConsole(["archive", "--input", input,
            "--staging-parent", Path.Combine(temp.Path, "stage"), "--staging-name", "run-1", "--archive-root", archive]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Verified:", stdout);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(archive, "2020", "photo-20200102.jpg")));
        Assert.Equal(new byte[] { 4 }, File.ReadAllBytes(Path.Combine(archive, "Unknown", "unknown.jpg")));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(photo))).ToLowerInvariant(),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(archive, "2020", "photo-20200102.jpg")))).ToLowerInvariant());
        var catalog = ArchiveCatalogStore.Read(archive);
        Assert.Equal(2, catalog.Items.Count);
        Assert.Contains("width", catalog.Items.Single(item => item.FileName == "photo-20200102.jpg").Unknowns);
        Assert.Contains("media-attribute-unknown", stderr);

        Directory.Delete(Path.Combine(temp.Path, "stage", "run-1"), recursive: true);
        var cliAssembly = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/MediaArchive.CLI/bin/Debug/net10.0/MediaArchive.CLI.dll"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(cliAssembly);
        start.ArgumentList.Add("browse");
        start.ArgumentList.Add("--archive-root");
        start.ArgumentList.Add(archive);
        using var browse = Process.Start(start)!;
        var browseOutput = browse.StandardOutput.ReadToEnd();
        var browseError = browse.StandardError.ReadToEnd();
        browse.WaitForExit();
        Assert.Equal(0, browse.ExitCode);
        Assert.Empty(browseError);
        Assert.Contains("year=2020", browseOutput);
        Assert.Contains("year=Unknown", browseOutput);
        Assert.Contains("metadata=Unknown", browseOutput);

        var secondInput = Directory.CreateDirectory(Path.Combine(temp.Path, "input-2")).FullName;
        File.WriteAllBytes(Path.Combine(secondInput, "photo-20200102.jpg"), [9]);
        var (secondExit, _, secondError) = RunWithConsole(["archive", "--input", secondInput,
            "--staging-parent", Path.Combine(temp.Path, "stage"), "--staging-name", "run-2", "--archive-root", archive]);
        Assert.Equal(1, secondExit);
        Assert.Contains("archive-copy-failed", secondError);
        Assert.Contains(Path.Combine(archive, "2020", "photo-20200102.jpg"), secondError);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(archive, "2020", "photo-20200102.jpg")));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(temp.Path, "stage", "run-2", "input-0001", "photo-20200102.jpg")));
    }

    [Fact]
    public void ArchiveCommandDoesNotIntegrateWhenStagingIsIncomplete()
    {
        using var temp = new TemporaryDirectory();
        var brokenZip = Path.Combine(temp.Path, "broken.zip");
        File.WriteAllText(brokenZip, "not a zip");
        var archive = Path.Combine(temp.Path, "archive");

        var (exitCode, stdout, stderr) = RunWithConsole(["archive", "--input", brokenZip,
            "--staging-parent", Path.Combine(temp.Path, "stage"), "--staging-name", "run-1", "--archive-root", archive]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Batch: incomplete", stdout);
        Assert.Contains("staging-failed", stderr);
        Assert.False(Directory.Exists(archive));
        Assert.Equal("not a zip", File.ReadAllText(brokenZip));
    }

    [Fact]
    public void ArchiveCommandRejectsSymlinkArchiveRootAndPreservesSource()
    {
        using var temp = new TemporaryDirectory();
        var input = Directory.CreateDirectory(Path.Combine(temp.Path, "input")).FullName;
        var source = Path.Combine(input, "photo-20200102.jpg");
        File.WriteAllBytes(source, [7, 8, 9]);
        var target = Directory.CreateDirectory(Path.Combine(temp.Path, "real-archive")).FullName;
        var link = Path.Combine(temp.Path, "linked-archive");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Symlink creation is unavailable in this environment: {ex.Message}");
        }

        var (exitCode, _, stderr) = RunWithConsole(["archive", "--input", input,
            "--staging-parent", Path.Combine(temp.Path, "stage"), "--staging-name", "run-1", "--archive-root", link]);

        Assert.Equal(1, exitCode);
        Assert.Contains("staging-failed", stderr);
        Assert.Empty(Directory.GetFiles(target, "*", SearchOption.AllDirectories));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(source));
    }

    private static (int ExitCode, string Stdout, string Stderr) RunWithConsole(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exitCode = InspectionCommand.Run(args);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

