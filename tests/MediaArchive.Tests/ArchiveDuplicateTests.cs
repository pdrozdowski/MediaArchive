using MediaArchive.Core;
using System.Security.Cryptography;

namespace MediaArchive.Tests;

public sealed class ArchiveDuplicateTests
{
    [Fact]
    public void SameBatchDuplicateWithDifferentNameAndYearReusesOneVerifiedFile()
    {
        var root = TempRoot();
        try
        {
            var staged = Path.Combine(root, "staged");
            Directory.CreateDirectory(Path.Combine(staged, "a"));
            Directory.CreateDirectory(Path.Combine(staged, "b"));
            var bytes = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(Path.Combine(staged, "a", "first.jpg"), bytes);
            File.WriteAllBytes(Path.Combine(staged, "b", "second.jpg"), bytes);
            var inputA = Path.Combine(root, "input-a");
            var inputB = Path.Combine(root, "input-b");
            var archive = Path.Combine(root, "archive");
            var inspection = new BatchInspectionResult([
                Item(inputA, "first.jpg", "2020", Path.Combine(archive, "2020", "first.jpg")),
                Item(inputB, "second.jpg", "2024", Path.Combine(archive, "2024", "second.jpg"))], [], [], DateTimeOffset.UtcNow);
            var staging = new BatchStagingResult(Path.Combine(root, "stage"), [
                new(inputA, Path.Combine(staged, "a"), true, null), new(inputB, Path.Combine(staged, "b"), true, null)]);

            var result = new ArchiveCopier().Copy(staging, inspection, archive);

            Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(d => d.Message)));
            Assert.Equal(2, result.VerifiedCopies.Count);
            Assert.Equal(result.VerifiedCopies[0].ArchivePath, result.VerifiedCopies[1].ArchivePath);
            Assert.Single(Directory.GetFiles(archive, "*.jpg", SearchOption.AllDirectories));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.VerifiedCopies[1].Sha256);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SamePathDifferentContentFailsWithoutOverwriting()
    {
        var root = TempRoot();
        try
        {
            var staged = Path.Combine(root, "staged"); Directory.CreateDirectory(staged);
            var source = Path.Combine(staged, "photo.jpg"); File.WriteAllBytes(source, [9, 9, 9]);
            var archive = Path.Combine(root, "archive"); Directory.CreateDirectory(Path.Combine(archive, "2020"));
            var existing = Path.Combine(archive, "2020", "photo.jpg"); File.WriteAllBytes(existing, [1, 2, 3]);
            var input = Path.Combine(root, "input");
            var result = new ArchiveCopier().Copy(new(Path.Combine(root, "stage"), [new(input, staged, true, null)]),
                new([Item(input, "photo.jpg", "2020", existing)], [], [], DateTimeOffset.UtcNow), archive);
            Assert.Empty(result.VerifiedCopies);
            Assert.Single(result.Diagnostics);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(existing));
            Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(source));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MalformedCatalogPreventsArchiveWrites()
    {
        var root = TempRoot();
        try
        {
            var staged = Path.Combine(root, "staged"); Directory.CreateDirectory(staged); File.WriteAllBytes(Path.Combine(staged, "x.jpg"), [1]);
            var archive = Path.Combine(root, "archive"); Directory.CreateDirectory(archive);
            File.WriteAllText(Path.Combine(archive, ArchiveCatalogStore.FileName), "not json");
            var input = Path.Combine(root, "input");
            var result = new ArchiveCopier().Copy(new(Path.Combine(root, "stage"), [new(input, staged, true, null)]),
                new([Item(input, "x.jpg", "2020", Path.Combine(archive, "2020", "x.jpg"))], [], [], DateTimeOffset.UtcNow), archive);
            Assert.Empty(result.VerifiedCopies);
            Assert.Single(result.Diagnostics);
            Assert.False(Directory.Exists(Path.Combine(archive, "2020")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("outside")]
    [InlineData("unreadable")]
    public void InvalidMatchingCatalogCandidateFailsClosedAndPreservesSources(string failure)
    {
        var root = TempRoot();
        try
        {
            var bytes = new byte[] { 1, 2, 3 };
            var staged = Path.Combine(root, "staged"); Directory.CreateDirectory(staged);
            var source = Path.Combine(staged, "new-name.jpg"); File.WriteAllBytes(source, bytes);
            var input = Path.Combine(root, "input");
            var archive = Path.Combine(root, "archive");
            var candidate = Path.Combine(archive, "2020", "old-name.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
            if (failure == "changed") File.WriteAllBytes(candidate, [9, 9, 9]);
            if (failure == "outside")
            {
                candidate = Path.Combine(root, "outside.jpg");
                File.WriteAllBytes(candidate, bytes);
            }
            if (failure == "unreadable") File.WriteAllBytes(candidate, bytes);
            var sidecar = new ArchiveCatalogEntry("old-input", "old-name.jpg", "old-name.jpg", "photo", "2020", candidate,
                null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), new(null, null, null, null), ["width", "height"]);
            ArchiveCatalogStore.Save(archive, [sidecar]);
            var item = Item(input, "new-name.jpg", "2024", Path.Combine(archive, "2024", "new-name.jpg"));
            var staging = new BatchStagingResult(Path.Combine(root, "stage"), [new(input, staged, true, null)]);
            var inspection = new BatchInspectionResult([item], [], [], DateTimeOffset.UtcNow);
            var copier = failure == "unreadable"
                ? new ArchiveCopier((path, mode, access, share) =>
                    path == candidate && access == FileAccess.Read
                        ? throw new UnauthorizedAccessException("injected candidate read denial")
                        : new FileStream(path, mode, access, share))
                : new ArchiveCopier();

            var result = copier.Copy(staging, inspection, archive);

            Assert.Empty(result.VerifiedCopies);
            Assert.Single(result.Diagnostics);
            Assert.Equal("archive-copy-failed", result.Diagnostics[0].Code);
            Assert.False(File.Exists(item.IntendedArchivePath));
            Assert.Equal(bytes, File.ReadAllBytes(source));
            Assert.Single(ArchiveCatalogStore.Read(archive).Items);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InvalidMatchingCatalogCandidateFallsBackToLaterVerifiedCandidate()
    {
        var root = TempRoot();
        try
        {
            var bytes = new byte[] { 1, 2, 3 };
            var staged = Path.Combine(root, "staged"); Directory.CreateDirectory(staged);
            var source = Path.Combine(staged, "new-name.jpg"); File.WriteAllBytes(source, bytes);
            var input = Path.Combine(root, "input");
            var archive = Path.Combine(root, "archive");
            var staleCandidate = Path.Combine(archive, "2020", "missing.jpg");
            var validCandidate = Path.Combine(archive, "2021", "valid.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(validCandidate)!);
            File.WriteAllBytes(validCandidate, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var stale = new ArchiveCatalogEntry("old-input-a", "missing.jpg", "missing.jpg", "photo", "2020", staleCandidate,
                null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, bytes.Length, hash, new(null, null, null, null), ["width"]);
            var valid = stale with { InputPath = "old-input-b", FileName = "valid.jpg", ArchivePath = validCandidate };
            ArchiveCatalogStore.Save(archive, [stale, valid]);
            var item = Item(input, "new-name.jpg", "2024", Path.Combine(archive, "2024", "new-name.jpg"));
            var staging = new BatchStagingResult(Path.Combine(root, "stage"), [new(input, staged, true, null)]);
            var inspection = new BatchInspectionResult([item], [], [], DateTimeOffset.UtcNow);

            var result = new ArchiveCopier().Copy(staging, inspection, archive);

            Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(d => d.Message)));
            Assert.Equal(validCandidate, Assert.Single(result.VerifiedCopies).ArchivePath);
            Assert.Equal(hash, result.VerifiedCopies[0].Sha256);
            Assert.False(File.Exists(item.IntendedArchivePath));
            Assert.Equal(bytes, File.ReadAllBytes(source));
            Assert.Single(Directory.GetFiles(archive, "*.jpg", SearchOption.AllDirectories));
            Assert.Equal(2, ArchiveCatalogStore.Read(archive).Items.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    private static InspectedMediaItem Item(string input, string path, string year, string target) =>
        new(input, path, "photo", null, [], year, target, DateTimeOffset.UtcNow, false);
    private static string TempRoot() { var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
}
