using System.IO.Compression;

namespace MediaArchive.Tests;

public sealed class CliInspectionTests
{
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
