using Sa.HybridFileStorage.Domain;
using System.Text;

namespace Sa.HybridFileStorage.PostgresTests;

public sealed class PostgresFileStorageTests(PostgresFileStorageTests.Fixture fixture)
    : IClassFixture<PostgresFileStorageTests.Fixture>
{
    private const string DataContent = "Hello, World!";

    public class Fixture : PostgresFileStorageFixture
    {
        public Fixture() : base("files") { }
    }

    private IFileStorage Sub => fixture.Sub;

    [Fact]
    public async Task UploadFileAsync()
    {
        Console.WriteLine(fixture.ConnectionString);

        // Arrange
        var input = new UploadFileInput { FileName = "test.txt", TenantId = 1 };
        using MemoryStream fileContent = await CreateStream(fixture.CancellationToken);


        // Act
        var result = await Sub.UploadAsync(input, fileContent, fixture.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.FileId);
        Assert.StartsWith("pg://share/1/", result.FileId);

        object? v = await fixture.DataSource.ExecuteScalar("SELECT COUNT(*) FROM public.files WHERE id = @id",
            cmd => cmd.Parameters.Add(new("id", result.FileId)), fixture.CancellationToken);
        var count = (long)v!;

        Assert.Equal(1, count);


        Assert.True(Sub.CanProcess(result.FileId));

        var meta = Sub.GetMetadataAsync(result.FileId, fixture.CancellationToken);
        Assert.NotNull(meta);
    }


    [Fact]
    public async Task DeleteFileAsync()
    {
        // Arrange
        using MemoryStream fileContent = await CreateStream(fixture.CancellationToken);
        var upload = await Sub.UploadAsync(
            new UploadFileInput { FileName = "test17.txt", TenantId = 17 }, fileContent, CancellationToken.None);

        // Act
        var result = await Sub.DeleteAsync(upload.FileId, CancellationToken.None);

        // Assert
        Assert.True(result);

        object? v = await fixture.DataSource
            .ExecuteScalar("SELECT COUNT(*) FROM public.files WHERE id = @id",
                cmd => cmd.Parameters.Add(new("id", upload.FileId)), fixture.CancellationToken);
        var count = (long)v!;

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DownloadFileAsync()
    {
        // Arrange
        using MemoryStream fileContent = await CreateStream(fixture.CancellationToken);

        var metadata = new UploadFileInput { FileName = "example.txt", TenantId = 123 };
        var upload = await Sub.UploadAsync(metadata, fileContent, CancellationToken.None);

        string? actual = null;

        // Act
        var result = await Sub.DownloadAsync(upload.FileId,
            async (s, t) => actual = await StreamToStringAsync(s), CancellationToken.None);

        // Assert
        Assert.True(result);

        Assert.Equal(DataContent, actual);
    }


    [Theory]
    [InlineData("pg://share/1/1773210911/a.txt", true)]
    [InlineData("pg://share/999/0/a.txt", true)]            // the tenant is not part of the claim
    [InlineData("s3://share/1/1773210911/a.txt", false)]    // another scheme
    [InlineData("pg://share2/1/1773210911/a.txt", false)]   // a sibling basket sharing the prefix
    [InlineData("pg://other/1/1773210911/a.txt", false)]    // another basket
    [InlineData("pg:/share/1/1773210911/a.txt", false)]     // broken scheme separator
    [InlineData("pg://share", false)]                       // no path segments
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CanProcess_ClaimsOnlyItsOwnSchemeAndBasket(string? fileId, bool expected)
        => Assert.Equal(expected, Sub.CanProcess(fileId!));

    [Fact]
    public async Task UploadAsync_FileNameWithNul_IsRejected()
    {
        // PostgreSQL text cannot hold a NUL byte, and the name reaches both the column and the ID.
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("x"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            Sub.UploadAsync(
                new UploadFileInput { FileName = "bad\0name.txt", TenantId = 1 },
                content, fixture.CancellationToken));

        Assert.Equal(nameof(UploadFileInput.FileName), ex.ParamName);
    }

    [Fact]
    public async Task GetMetadataAsync()
    {
        using var fileContent = await CreateStream(fixture.CancellationToken);

        var upload = await Sub.UploadAsync(
            new UploadFileInput { FileName = "some/data.bin", TenantId = 7 },
            fileContent, fixture.CancellationToken);

        var metadata = await Sub.GetMetadataAsync(upload.FileId, CancellationToken.None);

        Assert.NotNull(metadata);
        Assert.Equal("share", metadata.Basket);
        Assert.Equal(7, metadata.TenantId);
        Assert.Equal("some/data.bin", metadata.FileName);
        Assert.Equal("pg", metadata.StorageType);
    }

    [Fact]
    public async Task GetMetadataAsync_ReturnsNull_ForAWellFormedIdThatHasNoRow()
    {
        // A parsable file ID is not a file. Answering with fabricated metadata used to make this
        // storage win the hybrid chain for files that were deleted or never uploaded.
        var metadata = await Sub.GetMetadataAsync(
            "pg://share/7/1773210911/missing.bin", CancellationToken.None);

        Assert.Null(metadata);
    }

    [Fact]
    public async Task DownloadAsync_MissingFile_ReturnsFalse()
    {
        bool called = false;

        var found = await Sub.DownloadAsync(
            "pg://share/7/1773210911/missing.bin",
            (_, _) => { called = true; return Task.CompletedTask; },
            CancellationToken.None);

        Assert.False(found);
        Assert.False(called);
    }

    [Fact]
    public async Task DeleteAsync_MissingFile_ReturnsFalse()
    {
        var deleted = await Sub.DeleteAsync(
            "pg://share/7/1773210911/missing.bin", CancellationToken.None);

        Assert.False(deleted);
    }

    [Fact]
    public async Task DownloadAsync_AwaitsAnAsynchronousConsumer()
    {
        // The reader callback used to be passed to a synchronous Action overload, i.e. it ran as
        // `async void`: DownloadAsync returned before the consumer finished, and a failure in the
        // callback was unobservable. The gate makes that deterministic — the consumer is parked
        // inside the callback while the operation must still be pending.
        using var fileContent = await CreateStream(fixture.CancellationToken);

        var upload = await Sub.UploadAsync(
            new UploadFileInput { FileName = "awaited.txt", TenantId = 31 },
            fileContent, fixture.CancellationToken);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        string? actual = null;
        bool completed = false;

        Task<bool> download = Sub.DownloadAsync(upload.FileId, async (s, ct) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(ct);
            actual = await new StreamReader(s, Encoding.UTF8).ReadToEndAsync(ct);
            completed = true;
        }, fixture.CancellationToken);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), fixture.CancellationToken);

        // The operation may not finish while the consumer it handed the stream to is still working.
        // The wait is bounded, not a sleep: with the old implementation the reader loop advanced and
        // the operation completed as soon as the row was consumed (well under the bound), so the
        // check below is deterministic rather than a snapshot of one instant.
        bool completedWhileTheConsumerWasStillParked = false;

        for (int i = 0; i < 50; i++)
        {
            if (download.IsCompleted)
            {
                completedWhileTheConsumerWasStillParked = true;
                break;
            }

            await Task.Delay(10, fixture.CancellationToken);
        }

        Assert.False(completedWhileTheConsumerWasStillParked);

        release.SetResult();

        Assert.True(await download.WaitAsync(TimeSpan.FromSeconds(30), fixture.CancellationToken));
        Assert.True(completed);
        Assert.Equal(DataContent, actual);
    }

    [Fact]
    public async Task UploadAsync_NonSeekableStream_StoresTheWholeContent()
    {
        // A non-seekable stream is buffered first; the buffer used to be left at its end, and
        // Npgsql writes a Stream parameter from its current position — the row was stored empty
        // while `size` recorded the real length.
        byte[] payload = Encoding.UTF8.GetBytes(DataContent);

        await using var inner = new MemoryStream(payload);
        await using var nonSeekable = new NonSeekableStream(inner);

        var upload = await Sub.UploadAsync(
            new UploadFileInput { FileName = "nonseekable.txt", TenantId = 41 },
            nonSeekable, fixture.CancellationToken);

        long size = await fixture.DataSource.ExecuteScalar<long>(
            "SELECT size FROM public.files WHERE id = @id",
            cmd => cmd.Parameters.Add(new("id", upload.FileId)), fixture.CancellationToken);

        byte[] stored = await fixture.DataSource.ExecuteScalar<byte[]>(
            "SELECT data FROM public.files WHERE id = @id",
            cmd => cmd.Parameters.Add(new("id", upload.FileId)), fixture.CancellationToken);

        Assert.Equal(payload.Length, size);
        Assert.Equal(payload, stored);
    }

    [Fact]
    public async Task Upload2FileAsync()
    {
        Console.WriteLine(fixture.ConnectionString);

        // Arrange
        var metadata = new UploadFileInput { FileName = "test1.txt", TenantId = 1 };
        using MemoryStream first = await CreateStream(fixture.CancellationToken);
        using MemoryStream second = await CreateStream(fixture.CancellationToken);

        // The same ID from two concurrent uploads: one row, no error. Separate streams on purpose —
        // sharing one would race on the position the provider resets before inserting.
        var t1 = Sub.UploadAsync(metadata, first, fixture.CancellationToken);
        var t2 = Sub.UploadAsync(metadata, second, fixture.CancellationToken);

        await Task.WhenAll(t1, t2);

        var fileId1 = (await t1).FileId;
        var fileId2 = (await t2).FileId;

        Assert.Equal(fileId1, fileId2);

        long count = await fixture.DataSource.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM public.files WHERE id = @id",
            cmd => cmd.Parameters.Add(new("id", fileId1)), fixture.CancellationToken);

        Assert.Equal(1, count);
    }


    [Theory]
    [InlineData("./data/12345.wav")]
    public async Task UploadWavFileAsync(string filePath)
    {
        Console.WriteLine(fixture.ConnectionString);

        // Arrange
        var metadata = new UploadFileInput { FileName = filePath, TenantId = 1 };
        using var fileContent = File.OpenRead(filePath);

        var r = await Sub.UploadAsync(metadata, fileContent, fixture.CancellationToken);

        Assert.NotEmpty(r.FileId);
    }


    private static async Task<MemoryStream> CreateStream(CancellationToken cancellationToken)
    {
        var fileContent = new MemoryStream();
        var writer = new StreamWriter(fileContent);
        await writer.WriteAsync(DataContent);
        await writer.FlushAsync(cancellationToken);
        return fileContent;
    }


    public static async Task<string> StreamToStringAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
