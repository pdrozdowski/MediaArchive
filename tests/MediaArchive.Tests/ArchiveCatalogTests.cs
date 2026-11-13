using MediaArchive.Core;
using System.Diagnostics;

namespace MediaArchive.Tests;

public sealed class ArchiveCatalogTests
{
    [Fact]
    public void ProbeParsesKnownVideoAttributesFromFfprobeJson()
    {
        var probe = new MediaProbe(_ => """{"streams":[{"width":1920,"height":1080,"duration":"12.5","avg_frame_rate":"30000/1001"}],"format":{"duration":"12.5"}}""");

        var result = probe.Probe("clip.mp4", "video");

        Assert.Equal(1920, result.Attributes.Width);
        Assert.Equal(1080, result.Attributes.Height);
        Assert.Equal(12.5, result.Attributes.DurationSeconds);
        Assert.True(Math.Abs(30000d / 1001d - result.Attributes.FramesPerSecond!.Value) < 0.00000001d);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ProbeReturnsUnknownsForMalformedJsonAndUnavailableExecutable()
    {
        var malformed = new MediaProbe(_ => "not json").Probe("x.jpg", "photo");
        var unavailable = new MediaProbe("missing-ffprobe-executable-for-test").Probe("x.jpg", "photo");

        Assert.Null(malformed.Attributes.Width);
        Assert.NotEmpty(malformed.Diagnostics);
        Assert.Null(unavailable.Attributes.Height);
        Assert.NotEmpty(unavailable.Diagnostics);
    }

    [Fact]
    public void RealProbeProcessPersistsKnownAttributesForFreshBrowseProcess()
    {
        var root = TempRoot();
        try
        {
            var input = Path.Combine(root, "input");
            var staged = Path.Combine(root, "staged");
            var archive = Path.Combine(root, "archive");
            Directory.CreateDirectory(staged);
            Directory.CreateDirectory(archive);
            File.WriteAllBytes(Path.Combine(staged, "clip.mp4"), [1, 2, 3]);
            Directory.CreateDirectory(Path.Combine(archive, "2024"));
            var executable = Environment.GetEnvironmentVariable("WINDIR") is { } windows
                ? Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
                : "pwsh";
            const string json = "{\"streams\":[{\"width\":1920,\"height\":1080,\"duration\":\"12.5\",\"avg_frame_rate\":\"30000/1001\"}],\"format\":{\"duration\":\"12.5\"}}";
            var script = Path.Combine(root, "fake-ffprobe.ps1");
            File.WriteAllText(script, $"if ($args.Count -ne 9 -or $args[0] -ne '-v' -or $args[1] -ne 'error' -or $args[2] -ne '-select_streams' -or $args[3] -ne 'v:0' -or $args[4] -ne '-show_entries' -or $args[6] -ne '-of' -or $args[7] -ne 'json' -or -not $args[8].EndsWith('clip.mp4')) {{ [Console]::Error.WriteLine('unexpected arguments: ' + ($args -join '|')); exit 2 }}; Write-Output '{json}'");
            var probe = new MediaProbe(executable, ["-NoProfile", "-File", script]);
            var inspectionItem = new InspectedMediaItem(input, "clip.mp4", "video", null, [], "2024",
                Path.Combine(archive, "2024", "clip.mp4"), DateTimeOffset.UtcNow, false);
            var staging = new BatchStagingResult(Path.Combine(root, "staging"), [new(input, staged, true, null)]);
            var inspection = new BatchInspectionResult([inspectionItem], [], [], DateTimeOffset.UtcNow);
            var copies = new ArchiveCopyResult([new(input, "clip.mp4", inspectionItem.IntendedArchivePath, "abc")], []);

            var result = new ArchiveCatalogIntegrator(probe).Integrate(staging, inspection, copies, archive);

            Assert.Empty(result.Diagnostics);
            var saved = ArchiveCatalogStore.Read(archive).Items.Single();
            Assert.True(saved.Attributes.Width == 1920, string.Join(" | ", result.Warnings.Select(x => x.Message)));
            Assert.Equal(1080, saved.Attributes.Height);
            Assert.Equal(12.5, saved.Attributes.DurationSeconds);
            Assert.NotNull(saved.Attributes.FramesPerSecond);

            var cliAssembly = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/MediaArchive.CLI/bin/Debug/net10.0/MediaArchive.CLI.dll"));
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(cliAssembly);
            start.ArgumentList.Add("browse");
            start.ArgumentList.Add("--archive-root");
            start.ArgumentList.Add(archive);
            using var browse = Process.Start(start)!;
            var output = browse.StandardOutput.ReadToEnd();
            var error = browse.StandardError.ReadToEnd();
            browse.WaitForExit();
            Assert.Equal(0, browse.ExitCode);
            Assert.Empty(error);
            Assert.Contains("width=1920", output);
            Assert.Contains("height=1080", output);
            Assert.Contains("duration=12.5", output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ProbeDrainsLargeStandardErrorWithoutDeadlock()
    {
        var executable = Environment.GetEnvironmentVariable("WINDIR") is { } windows
            ? Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
            : "pwsh";
        var script = Path.Combine(Path.GetTempPath(), $"fake-ffprobe-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, "1..20000 | ForEach-Object { [Console]::Error.WriteLine('x' * 100) }; Write-Output '{\"streams\":[]}'");
        var probe = new MediaProbe(executable, ["-NoProfile", "-File", script]);

        MediaProbeResult result;
        try { result = probe.Probe("unused.jpg", "photo"); }
        finally { File.Delete(script); }

        Assert.Null(result.Attributes.Width);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void CatalogSaveFailsClosedOnMalformedExistingCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, ArchiveCatalogStore.FileName);
            File.WriteAllText(path, "{ malformed");
            var before = File.ReadAllBytes(path);

            Assert.Throws<System.Text.Json.JsonException>(() => ArchiveCatalogStore.Save(root, []));

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CatalogRejectsNullEntryAndPreservesMalformedFile()
    {
        var root = TempRoot();
        try
        {
            var path = Path.Combine(root, ArchiveCatalogStore.FileName);
            File.WriteAllText(path, "{\"SchemaVersion\":1,\"Items\":[null]}");
            var before = File.ReadAllBytes(path);
            Assert.Throws<InvalidDataException>(() => ArchiveCatalogStore.Read(root));
            Assert.Throws<InvalidDataException>(() => ArchiveCatalogStore.Save(root, [Entry("new.jpg")]));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CatalogReadFailureReportsEachUncatalogedVerifiedPath()
    {
        var root = TempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, ArchiveCatalogStore.FileName), "{\"SchemaVersion\":1,\"Items\":[null]}");
            var archivePath = Path.Combine(root, "2020", "new.jpg");
            var copies = new ArchiveCopyResult([new("input", "new.jpg", archivePath, "abc")], []);
            var staging = new BatchStagingResult(root, []);
            var inspection = new BatchInspectionResult([], [], [], DateTimeOffset.UtcNow);

            var result = new ArchiveCatalogIntegrator().Integrate(staging, inspection, copies, root);

            Assert.Contains(result.Diagnostics, d => d.Code == "catalog-read-failed" && d.RelativePath == "new.jpg" && d.Message.Contains(archivePath) && d.Message.Contains("uncataloged"));
            Assert.Equal("{\"SchemaVersion\":1,\"Items\":[null]}", File.ReadAllText(Path.Combine(root, ArchiveCatalogStore.FileName)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CatalogUpdatePreservesEarlierRecords()
    {
        var root = TempRoot();
        try
        {
            var first = Entry("first.jpg");
            var second = Entry("second.jpg");
            ArchiveCatalogStore.Save(root, [first]);
            ArchiveCatalogStore.Save(root, [second]);

            var catalog = ArchiveCatalogStore.Read(root);
            Assert.Equal(new[] { "first.jpg", "second.jpg" }, catalog.Items.Select(x => x.FileName));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CatalogWriteFailureReportsVerifiedCopyAsUncatalogedAndKeepsOldCatalog()
    {
        var root = TempRoot();
        try
        {
            var previous = Entry("previous.jpg");
            ArchiveCatalogStore.Save(root, [previous]);
            var originalCatalog = File.ReadAllBytes(Path.Combine(root, ArchiveCatalogStore.FileName));
            var input = Path.Combine(root, "input");
            var staged = Path.Combine(root, "staged");
            Directory.CreateDirectory(staged);
            var stagedFile = Path.Combine(staged, "new.jpg");
            File.WriteAllBytes(stagedFile, [1, 2, 3]);
            var sourcePath = Path.Combine(input, "source");
            var inspectionItem = new InspectedMediaItem(input, "new.jpg", "photo", null, [], "2020",
                Path.Combine(root, "2020", "new.jpg"), DateTimeOffset.UtcNow, false);
            var staging = new BatchStagingResult(Path.Combine(root, "stage"), [new(input, staged, true, null)]);
            var inspection = new BatchInspectionResult([inspectionItem], [], [], DateTimeOffset.UtcNow);
            var copies = new ArchiveCopyResult([new(input, "new.jpg", Path.Combine(root, "2020", "new.jpg"), "abc")], []);
            var integrator = new ArchiveCatalogIntegrator(
                new MediaProbe(_ => "{}"),
                (_, _) => throw new IOException("injected catalog write failure"));

            var result = integrator.Integrate(staging, inspection, copies, root);

            Assert.Contains(result.Diagnostics, d => d.Code == "catalog-write-failed" && d.Message.Contains("remains uncataloged"));
            Assert.Equal(originalCatalog, File.ReadAllBytes(Path.Combine(root, ArchiveCatalogStore.FileName)));
            Assert.Equal("previous.jpg", ArchiveCatalogStore.Read(root).Items.Single().FileName);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(stagedFile));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static ArchiveCatalogEntry Entry(string filename) => new("input", filename, filename, "photo", "Unknown",
        filename, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, "abc", new(null, null, null, null), ["width", "height"]);
}
