using Sa.HybridFileStorage.Postgres;

namespace Sa.HybridFileStorage.PostgresTests;

public class FileIdParserTests
{
    [Fact]
    public void ParseFromFileId_ValidFileId_ReturnsTenantIdAndTimestamp()
    {
        string fileId = "pg://files/123/1773210911/foo/some.txt";
        var result = FileIdParser.TryParseFileIdWithFilename(
            fileId,
            out int tenantId,
            out long timestamp,
            out string filename);

        Assert.True(result);
        Assert.Equal(123, tenantId);
        Assert.Equal(1773210911, timestamp);
        Assert.Equal("foo/some.txt", filename);
    }

    [Fact]
    public void ParseFromFileId_InvalidFileId()
    {
        string fileId = "invalid_file_id";
        var result = FileIdParser.TryParseFileIdWithFilename(
            fileId,
            out _,
            out _,
            out _);

        Assert.False(result);
    }

    [Fact]
    public void FormatToFileId_ValidParameters_ReturnsFormattedFileId()
    {
        string storageType = "pg";
        int tenantId = 123;
        DateTimeOffset date = new(2023, 10, 5, 12, 0, 0, TimeSpan.Zero);
        string fileName = "example.txt";
        string result = FileIdParser.FormatToFileId(storageType, "files", tenantId, date, fileName);
        Assert.Equal($"pg://files/123/{date.ToUnixTimeSeconds()}/example.txt", result);
    }

    [Fact]
    public void NormalizeFileName_ValidFileName_ReturnsNormalizedFileName()
    {
        string fileName = "\\path\\to\\file.txt";
        string result = FileIdParser.NormalizeFileName(fileName);
        Assert.Equal("path/to/file.txt", result);
    }

    [Fact]
    public void GetFileExtension_ValidFileName_ReturnsFileExtension()
    {
        string fileName = "example.txt";
        string result = FileIdParser.GetFileExtension(fileName);
        Assert.Equal("txt", result);
    }
}
