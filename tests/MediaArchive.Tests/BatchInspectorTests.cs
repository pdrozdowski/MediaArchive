using System.IO.Compression;
using MediaArchive.Core;

namespace MediaArchive.Tests;

public sealed class BatchInspectorTests
{
    [Fact]
    public void PreservesSidecarFieldsAndUsesUtcMetadataYearBeforeFilename()
    {
        using var temp = new TemporaryDirectory();
        var input = temp.AddInput("part-1");
        File.WriteAllBytes(Path.Combine(input, "photo-19991231.jpg"), [1, 2]);
        File.WriteAllText(Path.Combine(input, "photo-19991231.json"), """
            {"title":"photo-19991231.jpg","photoTakenTime":{"timestamp":"1609459200"},"location":{"latitude":12.5},"extra":"kept"}
            """);

        var result = new BatchInspector().Inspect(temp.Staging([input]), Path.Combine(temp.Path, "archive"));
        var item = Assert.Single(result.Items);

        Assert.Equal("photo", item.MediaKind);
        Assert.Equal("photo-19991231.jpg", item.RelativePath);
        Assert.Equal(input, item.InputPath);
        Assert.Equal("2021", item.Year);
        Assert.Equal(Path.Combine(temp.Path, "archive", "2021", "photo-19991231.jpg"), item.IntendedArchivePath);
        Assert.Equal("kept", item.Sidecar!.Value.GetProperty("extra").GetString());
        Assert.Empty(item.Unknowns);
        Assert.Equal(TimeSpan.Zero, item.PreparedAtUtc.Offset);
        Assert.Equal(result.PreparedAtUtc, item.PreparedAtUtc);
    }

    [Theory]
    [InlineData("missing", "photo-20010504.jpg", "2001")]
    [InlineData("zero", "photo-20010504.jpg", "2001")]
    [InlineData("malformed", "photo-20010504.jpg", "2001")]
    [InlineData("out-of-range", "photo-20010504.jpg", "2001")]
    public void FallsBackToFilenameWhenMetadataTimestampIsUnusable(string timestampKind, string filename, string expectedYear)
    {
        using var temp = new TemporaryDirectory();
        var input = temp.AddInput("part");
        File.WriteAllBytes(Path.Combine(input, filename), [1]);
        var timestamp = timestampKind switch
        {
            "zero" => "0",
            "malformed" => "not-a-number",
            "out-of-range" => "99999999999999999999",
            _ => null
        };
        var date = timestamp is null ? "{}" : $$"""{"timestamp":"{{timestamp}}"}""";
        File.WriteAllText(Path.Combine(input, Path.GetFileNameWithoutExtension(filename) + ".json"),
            $$"""{"title":"{{filename}}","photoTakenTime":{{date}}}""");

        var result = new BatchInspector().Inspect(temp.Staging([input]), Path.Combine(temp.Path, "archive"));

        Assert.Equal(expectedYear, Assert.Single(result.Items).Year);
    }

    [Theory]
    [InlineData("image-20200230.jpg")]
    [InlineData("image-20200101-copy-20210101.jpg")]
    [InlineData("image-no-date.jpg")]
    public void UsesUnknownYearForInvalidAmbiguousOrMissingFilenameDates(string filename)
    {
        using var temp = new TemporaryDirectory();
        var input = temp.AddInput("part");
        File.WriteAllBytes(Path.Combine(input, filename), [1]);

        var item = Assert.Single(new BatchInspector().Inspect(temp.Staging([input]), Path.Combine(temp.Path, "archive")).Items);

        Assert.Equal("Unknown", item.Year);
        Assert.Contains("year", item.Unknowns);
        Assert.EndsWith(Path.Combine("Unknown", filename), item.IntendedArchivePath);
    }

    [Fact]
    public void ReportsMissingMalformedCompetingAndCrossPartMetadata()
    {
        using var temp = new TemporaryDirectory();
        var one = temp.AddInput("part-1");
        var two = temp.AddInput("part-2");
        File.WriteAllBytes(Path.Combine(one, "missing.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(one, "broken.jpg"), [1]);
        File.WriteAllText(Path.Combine(one, "broken.json"), "{");
        File.WriteAllBytes(Path.Combine(one, "competing.jpg"), [1]);
        File.WriteAllText(Path.Combine(one, "meta-a.json"), "{\"title\":\"competing.jpg\"}");
        File.WriteAllText(Path.Combine(one, "meta-b.json"), "{\"title\":\"competing.jpg\"}");
        File.WriteAllBytes(Path.Combine(one, "cross.jpg"), [1]);
        File.WriteAllText(Path.Combine(two, "cross.json"), "{\"title\":\"cross.jpg\"}");

        var result = new BatchInspector().Inspect(temp.Staging([one, two]), Path.Combine(temp.Path, "archive"));
        var codes = result.Diagnostics.Select(diagnostic => diagnostic.Code).ToHashSet();

        Assert.Contains("sidecar-missing", codes);
        Assert.Contains("sidecar-malformed", codes);
        Assert.Contains("sidecar-competing", codes);
        Assert.Contains("sidecar-cross-part", codes);
        Assert.Contains("sidecar-cross-part-unresolved", codes);
        Assert.Contains("sidecar-unresolved", codes);
        Assert.All(result.Items.Where(item => item.Sidecar is null), item => Assert.Contains("sidecar", item.Unknowns));
    }

    [Fact]
    public void ReportsUnsupportedFilesAndProspectivePathCollisions()
    {
        using var temp = new TemporaryDirectory();
        var first = temp.AddInput("part-a");
        var second = temp.AddInput("part-b");
        File.WriteAllBytes(Path.Combine(first, "same-20200101.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(second, "same-20200101.jpg"), [2]);
        File.WriteAllText(Path.Combine(first, "notes.txt"), "ignored as media");

        var result = new BatchInspector().Inspect(temp.Staging([first, second]), Path.Combine(temp.Path, "archive"));

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.True(item.IntendedPathCollision));
        Assert.Equal(2, result.Diagnostics.Count(diagnostic => diagnostic.Code == "intended-path-collision"));
        Assert.Contains(result.UnrecognizedFiles, path => path.EndsWith(":notes.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void RecognizesSupportedExtensionsCaseInsensitivelyAndCarriesStagingFailures()
    {
        using var temp = new TemporaryDirectory();
        var input = temp.AddInput("part");
        File.WriteAllBytes(Path.Combine(input, "photo.HEIC"), [1]);
        File.WriteAllBytes(Path.Combine(input, "clip.WebM"), [1]);
        var originalStaging = temp.Staging([input]);
        var staged = originalStaging with
        {
            Inputs = [.. originalStaging.Inputs, new BatchInputResult("failed.zip", "", false, "bad zip")]
        };

        var result = new BatchInspector().Inspect(staged, Path.Combine(temp.Path, "archive"));

        Assert.Contains(result.Items, item => item.MediaKind == "photo");
        Assert.Contains(result.Items, item => item.MediaKind == "video");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "staging-failed");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public string AddInput(string name)
        {
            var input = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(input);
            return input;
        }
        public BatchStagingResult Staging(IEnumerable<string> inputs)
        {
            var results = inputs.Select(input => new BatchInputResult(input, input, true, null)).ToArray();
            return new(System.IO.Path.Combine(Path, "staging"), results);
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
