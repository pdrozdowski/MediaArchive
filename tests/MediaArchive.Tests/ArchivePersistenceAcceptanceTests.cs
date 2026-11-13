using System.Diagnostics;
using System.Text.Json;
using MediaArchive.Core;

namespace MediaArchive.Tests;

public sealed class ArchivePersistenceAcceptanceTests
{
    [Fact]
    public void FreshCoreReaderRetainsAllRecordsAndMetadataAfterStagingRemoval()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(root, "archive");
        var stagingParent = Path.Combine(root, "staging");
        var firstInput = Path.Combine(root, "input-a");
        var secondInput = Path.Combine(root, "input-b");
        Directory.CreateDirectory(firstInput);
        Directory.CreateDirectory(secondInput);
        try
        {
            var photo = Path.Combine(firstInput, "photo-20240102.jpg");
            var clip = Path.Combine(secondInput, "clip.mp4");
            File.WriteAllBytes(photo, [1, 2, 3]);
            File.WriteAllBytes(clip, [4, 5, 6, 7]);
            File.WriteAllText(Path.Combine(firstInput, "photo-20240102.json"), "{\"title\":\"photo-20240102.jpg\",\"extra\":{\"label\":\"original\"}}");
            var probe = new MediaProbe(path => path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                ? "{\"streams\":[{\"width\":320,\"height\":240}]}" : "{\"streams\":[]}");
            foreach (var (input, run) in new[] { (firstInput, "run-1"), (secondInput, "run-2") })
            {
                var staged = new BatchStager().Stage([input], stagingParent, archive, run);
                Assert.True(staged.IsComplete);
                var inspected = new BatchInspector().Inspect(staged, archive);
                var copied = new ArchiveCopier().Copy(staged, inspected, archive);
                Assert.True(copied.Succeeded);
                var integrated = new ArchiveCatalogIntegrator(probe).Integrate(staged, inspected, copied, archive);
                Assert.Empty(integrated.Diagnostics);
            }
            var before = ArchiveCatalogStore.Read(archive);
            Assert.Equal(2, before.Items.Count);
            var photoEntry = Assert.Single(before.Items, x => x.FileName == "photo-20240102.jpg");
            var clipEntry = Assert.Single(before.Items, x => x.FileName == "clip.mp4");
            Assert.Equal(firstInput, photoEntry.InputPath);
            Assert.Equal("original", photoEntry.OriginalMetadata!.Value.GetProperty("extra").GetProperty("label").GetString());
            Assert.Equal(320, photoEntry.Attributes.Width);
            Assert.Equal(240, photoEntry.Attributes.Height);
            Assert.Equal("2024", photoEntry.Year);
            Assert.Equal(secondInput, clipEntry.InputPath);
            Assert.Equal("Unknown", clipEntry.Year);
            Assert.Null(clipEntry.OriginalMetadata);
            Assert.Null(clipEntry.Attributes.Width);
            Assert.Null(clipEntry.Attributes.DurationSeconds);
            Assert.Contains("framesPerSecond", clipEntry.Unknowns);
            Assert.Equal(File.ReadAllBytes(photo), File.ReadAllBytes(photoEntry.ArchivePath));
            Assert.Equal(File.ReadAllBytes(clip), File.ReadAllBytes(clipEntry.ArchivePath));
            Directory.Delete(stagingParent, true);
            var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../ArchiveCatalogReader/ArchiveCatalogReader.csproj"));
            var (exit, output, error) = Run("dotnet", "run", "--project", project, "--", archive);
            Assert.True(exit == 0, error);
            var expected = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(before));
            var actual = System.Text.Json.Nodes.JsonNode.Parse(output);
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(expected, actual), "Fresh Core reader must retain every field and record.");
        }
        finally { Directory.Delete(root, true); }
    }

    [FfprobeFact]
    public void ActualFfprobeReadsKnownPhotoAndVideoFixtures()
    {
var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "input");
            var staging = Path.Combine(root, "staging");
            var archive = Path.Combine(root, "archive");
            Directory.CreateDirectory(input);
            var photo = Path.Combine(input, "photo.png");
            var video = Path.Combine(input, "video.mp4");
            var image = Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "color=c=red:s=32x24", "-frames:v", "1", photo);
            Assert.True(image.Exit == 0, image.Error);
            var clip = Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=32x24:r=5:d=2", "-c:v", "mpeg4", video);
            Assert.True(clip.Exit == 0, clip.Error);
            var p = new MediaProbe().Probe(photo, "photo");
            var v = new MediaProbe().Probe(video, "video");
            Assert.True(p.Diagnostics.Count == 0, string.Join("; ", p.Diagnostics));
            Assert.Equal(32, p.Attributes.Width); Assert.Equal(24, p.Attributes.Height);
            Assert.Null(p.Attributes.DurationSeconds); Assert.Null(p.Attributes.FramesPerSecond);
            Assert.True(v.Diagnostics.Count == 0, string.Join("; ", v.Diagnostics));
            Assert.Equal(32, v.Attributes.Width); Assert.Equal(24, v.Attributes.Height);
            Assert.Equal(2d, v.Attributes.DurationSeconds); Assert.Equal(5d, v.Attributes.FramesPerSecond);

            var staged = new BatchStager().Stage([input], staging, archive, "run");
            Assert.True(staged.IsComplete);
            var inspected = new BatchInspector().Inspect(staged, archive);
            var copies = new ArchiveCopier().Copy(staged, inspected, archive);
            Assert.True(copies.Succeeded);
            var integrated = new ArchiveCatalogIntegrator().Integrate(staged, inspected, copies, archive);
            Assert.Empty(integrated.Diagnostics);
            var expected = ArchiveCatalogStore.Read(archive);
            Assert.Equal(2, expected.Items.Count);
            var savedPhoto = Assert.Single(expected.Items, x => x.FileName == "photo.png");
            var savedVideo = Assert.Single(expected.Items, x => x.FileName == "video.mp4");
            Assert.Equal(32, savedPhoto.Attributes.Width); Assert.Equal(24, savedPhoto.Attributes.Height);
            Assert.Null(savedPhoto.Attributes.DurationSeconds); Assert.Null(savedPhoto.Attributes.FramesPerSecond);
            Assert.Equal(32, savedVideo.Attributes.Width); Assert.Equal(24, savedVideo.Attributes.Height);
            Assert.Equal(2d, savedVideo.Attributes.DurationSeconds); Assert.Equal(5d, savedVideo.Attributes.FramesPerSecond);
            Directory.Delete(staging, true);
            var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../ArchiveCatalogReader/ArchiveCatalogReader.csproj"));
            var (exit, output, error) = Run("dotnet", "run", "--project", project, "--", archive);
            Assert.True(exit == 0, error);
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(expected)),
                System.Text.Json.Nodes.JsonNode.Parse(output)), "Fresh Core reader must retain actual probe values and every catalog field.");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FfprobeFactAttribute : FactAttribute
    {
        public FfprobeFactAttribute()
        {
            if (!Available("ffprobe") || !Available("ffmpeg")) Skip = "Actual media fixture check requires ffprobe and ffmpeg on PATH.";
        }
    }

    private static bool Available(string program)
    {
        try { return Run(program, "-version").Exit == 0; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static (int Exit, string Output, string Error) Run(string command, params string[] args)
    {
        var start = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60000), $"{command} timed out");
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}



