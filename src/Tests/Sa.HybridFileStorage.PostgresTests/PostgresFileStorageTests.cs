using Sa.HybridFileStorage.Domain;
using System.Text;

namespace Sa.HybridFileStorage.PostgresTests;

public class PostgresFileStorageTests(PostgresFileStorageTests.Fixture fixture)
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


    [Fact]
    public async Task GetMetadataAsync()
    {
        var metadata = await Sub.GetMetadataAsync("pg://share/7/1773210911/some/data.bin", CancellationToken.None);

        Assert.NotNull(metadata);
        Assert.Equal("share", metadata.Basket);
        Assert.Equal(7, metadata.TenantId);
        Assert.Equal("some/data.bin", metadata.FileName);
    }


    [Fact]
    public async Task Upload2FileAsync()
    {
        Console.WriteLine(fixture.ConnectionString);

        // Arrange
        var metadata = new UploadFileInput { FileName = "test1.txt", TenantId = 1 };
        using MemoryStream fileContent = await CreateStream(fixture.CancellationToken);

        var t1 = Sub.UploadAsync(metadata, fileContent, fixture.CancellationToken);
        var t2 = Sub.UploadAsync(metadata, fileContent, fixture.CancellationToken);

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
