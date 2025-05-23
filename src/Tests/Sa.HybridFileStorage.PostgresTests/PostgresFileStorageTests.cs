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
        var metadata = new UploadFileInput { FileName = "test.txt", TenantId = 1 };
        using MemoryStream fileContent = await CreateStream(fixture.CancellationToken);


        // Act
        var result = await Sub.UploadAsync(metadata, fileContent, fixture.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.FileId);

        object? v = await fixture.DataSource.ExecuteScalar("SELECT COUNT(*) FROM public.files WHERE id = @id", [new("id", result.FileId)], fixture.CancellationToken);
        var count = (long)v!;

        Assert.Equal(1, count);
    }


    [Fact]
    public async Task DeleteFileAsync()
    {
        // Arrange
        using MemoryStream fileContent = await CreateStream(fixture.CancellationToken);
        var upload = await Sub.UploadAsync(new UploadFileInput { FileName = "test17.txt", TenantId = 17 }, fileContent, CancellationToken.None);

        // Act
        var result = await Sub.DeleteAsync(upload.FileId, CancellationToken.None);

        // Assert
        Assert.True(result);

        object? v = await fixture.DataSource.ExecuteScalar("SELECT COUNT(*) FROM public.files WHERE id = @id", [new("id", upload.FileId)], fixture.CancellationToken);
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
        var result = await Sub.DownloadAsync(upload.FileId, async (s, t) => actual = await StreamToStringAsync(s), CancellationToken.None);

        // Assert
        Assert.True(result);

        Assert.Equal(DataContent, actual);
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