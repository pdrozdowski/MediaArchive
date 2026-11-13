using MediaArchive.Core;

namespace MediaArchive.Tests;

public sealed class ArchiveCopyFailureTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("copy")]
    [InlineData("mismatch")]
    public void CopyFailuresPreserveSourcesAndNeverProduceVerifiedCatalogEntry(string failure)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        var staged = Path.Combine(root, "staged");
        var archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(staged);
        var bytes = new byte[] { 1, 2, 3, 4 };
        var original = Path.Combine(input, "photo.jpg");
        var source = Path.Combine(staged, "photo.jpg");
        var target = Path.Combine(archive, "2020", "photo.jpg");
        File.WriteAllBytes(original, bytes);
        File.WriteAllBytes(source, bytes);
        try
        {
            var staging = new BatchStagingResult(root, [new(input, staged, true, null)]);
            var item = new InspectedMediaItem(input, "photo.jpg", "photo", null, [], "2020", target, DateTimeOffset.UtcNow, false);
            var inspection = new BatchInspectionResult([item], [], [], DateTimeOffset.UtcNow);
            var copier = new ArchiveCopier((path, mode, access, share) =>
            {
                if (failure == "read" && path == source) throw new IOException("injected read failure");
                if (failure == "copy" && mode == FileMode.CreateNew)
                    return new FailingWriteStream(new FileStream(path, mode, access, share));
                if (failure == "mismatch" && path == target && access == FileAccess.Read)
                    return new MemoryStream([9, 9, 9]);
                return new FileStream(path, mode, access, share);
            });

            var copies = copier.Copy(staging, inspection, archive);
            Assert.Empty(copies.VerifiedCopies);
            var diagnostic = Assert.Single(copies.Diagnostics);
            Assert.Equal("archive-copy-failed", diagnostic.Code);
            Assert.Equal("photo.jpg", diagnostic.RelativePath);
            Assert.Contains(failure == "mismatch" ? "SHA-256 verification failed" : $"injected {failure} failure", diagnostic.Message);
            if (failure != "read") Assert.Contains(target, diagnostic.Message);
            Assert.Equal(bytes, File.ReadAllBytes(original));
            Assert.Equal(bytes, File.ReadAllBytes(source));
            var catalog = new ArchiveCatalogIntegrator().Integrate(staging, inspection, copies, archive);
            Assert.Empty(catalog.Catalog.Items);
            Assert.False(File.Exists(Path.Combine(archive, ArchiveCatalogStore.FileName)));
            if (failure == "copy") Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class FailingWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, Math.Min(1, count));
            throw new IOException("injected copy failure");
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
