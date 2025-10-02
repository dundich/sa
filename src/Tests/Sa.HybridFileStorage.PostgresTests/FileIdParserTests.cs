using Sa.HybridFileStorage.Postgres;

namespace Sa.HybridFileStorage.PostgresTests;

public class FileIdParserTests
{
    [Fact]
    public void ParseFromFileId_ValidFileId_ReturnsTenantIdAndTimestamp()
    {
        string fileId = "pg://123/2023/10/05/12/foo/some.txt";
        var (tenantId, timestamp) = FileIdParser.ParseFromFileId(fileId);
        Assert.Equal(123, tenantId);
        Assert.Equal(new DateTimeOffset(2023, 10, 5, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), timestamp);
    }

    [Fact]
    public void ParseFromFileId_InvalidFileId_ThrowsFormatException()
    {
        string fileId = "invalid_file_id";
        Assert.Throws<FormatException>(() => FileIdParser.ParseFromFileId(fileId));
    }

    [Fact]
    public void FormatToFileId_ValidParameters_ReturnsFormattedFileId()
    {
        string storageType = "pg";
        int tenantId = 123;
        DateTimeOffset date = new(2023, 10, 5, 12, 0, 0, TimeSpan.Zero);
        string fileName = "example.txt";
        string result = FileIdParser.FormatToFileId(storageType, tenantId, date, fileName);
        Assert.Equal("pg://123/2023/10/05/12/example.txt", result);
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