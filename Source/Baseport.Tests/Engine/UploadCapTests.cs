using Xunit;
using Baseport;
using Microsoft.AspNetCore.Http;

namespace Baseport.Tests;

[CollectionDefinition(nameof(UploadCapTests), DisableParallelization = true)]
public class UploadCapCollection;

[Collection(nameof(UploadCapTests))]
public class UploadCapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "baseport-cap-" + Guid.NewGuid().ToString("N"));
    private readonly long _savedCap = FileStore.CapBytes;
    private readonly long _savedFree = FileStore.MinFreeBytes;

    public UploadCapTests()
    {
        Directory.CreateDirectory(_root);
        FileStore.Initialize($"Data Source={Path.Combine(_root, "baseport.db")}");
    }

    public void Dispose()
    {
        FileStore.CapBytes = _savedCap;
        FileStore.MinFreeBytes = _savedFree;
        FileStore.Initialize("Data Source=baseport.db");
        Directory.Delete(_root, recursive: true);
    }

    private static FormFile File(int size) =>
        new(new MemoryStream(new byte[size]), 0, size, "file", "note.txt");

    [Fact]
    public async Task An_upload_past_the_instance_cap_is_refused_and_nothing_is_written()
    {
        FileStore.CapBytes = 100;
        var (first, firstError) = await FileStore.SaveAsync(File(60), "docs", TestContext.Current.CancellationToken);
        Assert.Null(firstError);

        var (second, error) = await FileStore.SaveAsync(File(60), "docs", TestContext.Current.CancellationToken);

        Assert.Null(second);
        Assert.Contains("storage is full", error);
        Assert.Single(FileStore.AllStoredNames(), first);
    }

    [Fact]
    public void A_form_upload_is_held_to_the_same_cap()
    {
        FileStore.CapBytes = 10;
        Assert.Contains("storage is full", FileStore.Problem(File(20)));
    }

    [Fact]
    public async Task Deleting_a_file_frees_its_bytes()
    {
        FileStore.CapBytes = 100;
        var (stored, _) = await FileStore.SaveAsync(File(60), "docs", TestContext.Current.CancellationToken);

        FileStore.Delete(stored!);

        Assert.Equal(0, FileStore.UsedBytes);
        Assert.Null(FileStore.Problem(File(60), "docs"));
    }

    [Fact]
    public async Task The_used_total_is_rebuilt_from_disk_on_start()
    {
        await FileStore.SaveAsync(File(42), "docs", TestContext.Current.CancellationToken);

        FileStore.Initialize($"Data Source={Path.Combine(_root, "baseport.db")}");

        Assert.Equal(42, FileStore.UsedBytes);
    }

    [Fact]
    public void An_upload_that_would_leave_too_little_free_disk_is_refused()
    {
        FileStore.MinFreeBytes = long.MaxValue / 2;
        Assert.Contains("low on disk space", FileStore.Problem(File(1)));
    }

    [Fact]
    public async Task CancelledUploadLeavesNoFile()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var name = FileStore.Reserve(File(10), "docs");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileStore.WriteAsync(File(10), name, cancelled.Token));

        Assert.Empty(System.IO.Directory.EnumerateFiles(FileStore.Directory, "*", SearchOption.AllDirectories));
        Assert.Equal(0, FileStore.UsedBytes);
    }

    [Fact]
    public void PartialFilesAreNeverServed()
    {
        Assert.Null(FileStore.Resolve("abc.txt" + FileStore.PartialSuffix));
        Assert.Null(FileStore.Resolve("docs/abc.txt" + FileStore.PartialSuffix));
        Assert.False(new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider().TryGetContentType("abc.txt" + FileStore.PartialSuffix, out _));
    }

    [Fact]
    public async Task SweepSkipsYoungFiles()
    {
        var name = FileStore.Reserve(File(5));
        await FileStore.WriteAsync(File(5), name, TestContext.Current.CancellationToken);

        Assert.Empty(FileStore.SweepCandidates(DateTime.UtcNow));
        Assert.Equal([name], FileStore.SweepCandidates(DateTime.UtcNow + FileStore.Grace + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void SweepRemovesStalePartials()
    {
        var bucket = Path.Combine(FileStore.Directory, "docs");
        System.IO.Directory.CreateDirectory(bucket);
        var stale = Path.Combine(bucket, "old.txt" + FileStore.PartialSuffix);
        var young = Path.Combine(FileStore.Directory, "new.txt" + FileStore.PartialSuffix);
        System.IO.File.WriteAllText(stale, "x");
        System.IO.File.WriteAllText(young, "x");
        System.IO.File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - FileStore.Grace - TimeSpan.FromMinutes(1));

        Assert.Equal(1, FileStore.DeleteStalePartials(DateTime.UtcNow));
        Assert.False(System.IO.File.Exists(stale));
        Assert.True(System.IO.File.Exists(young));
        Assert.Empty(FileStore.AllStoredNames());
    }
}
