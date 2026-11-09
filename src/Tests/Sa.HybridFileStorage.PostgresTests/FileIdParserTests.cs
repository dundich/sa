using Sa.HybridFileStorage;

namespace Sa.HybridFileStorage.PostgresTests;

public class FileIdParserTests
{
    [Fact]
    public void ParseFromFileId_ValidFileId_ReturnsTenantIdAndTimestamp()
    {
        string fileId = "pg://files/123/1773210911/foo/some.txt";
        var result = FileIdParser.TryParse(
            fileId,
            out string scopeName,
            out int tenantId,
            out long timestamp,
            out string filename);

        Assert.True(result);
        Assert.Equal(123, tenantId);
        Assert.Equal("files", scopeName);
        Assert.Equal(1773210911, timestamp);
        Assert.Equal("foo/some.txt", filename);
    }

    [Fact]
    public void ParseFromFileId_InvalidFileId()
    {
        string fileId = "invalid_file_id";
        var result = FileIdParser.TryParse(
            fileId,
            out _,
            out _,
            out _,
            out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParse_SimpleFormat_WithTimestampLayout_KeepsTheNumericDirectoryInTheName()
    {
        // A no-timestamp provider legitimately stores a file in a directory named with a number.
        // The heuristic reads "2024" as the Postgres timestamp; the explicit layout must not.
        string fileId = "fs://share/1/2024/report.pdf";

        Assert.True(FileIdParser.TryParse(
            fileId, FileIdFormat.WithoutTimestamp,
            out var basket, out var tenantId, out var timestamp, out var fileName));

        Assert.Equal("share", basket);
        Assert.Equal(1, tenantId);
        Assert.Equal(0, timestamp);
        Assert.Equal("2024/report.pdf", fileName);

        // The two-argument overload documents the ambiguity it cannot resolve.
        Assert.True(FileIdParser.TryParse(fileId, out _, out _, out var heuristicTimestamp, out var heuristicName));
        Assert.Equal(2024, heuristicTimestamp);
        Assert.Equal("report.pdf", heuristicName);
    }

    [Fact]
    public void TryParse_TimestampLayout_RejectsAFileIdWithoutATimestampSegment()
    {
        Assert.False(FileIdParser.TryParse(
            "fs://share/1/report.pdf", FileIdFormat.WithTimestamp, out _, out _, out _, out _));

        Assert.False(FileIdParser.TryParse(
            "fs://share/1/not-a-number/report.pdf", FileIdFormat.WithTimestamp, out _, out _, out _, out _));
    }

    [Fact]
    public void TryParse_TimestampLayout_ReadsThePostgresShape()
    {
        Assert.True(FileIdParser.TryParse(
            "pg://files/12/1751347200/photo.jpg", FileIdFormat.WithTimestamp,
            out var basket, out var tenantId, out var timestamp, out var fileName));

        Assert.Equal("files", basket);
        Assert.Equal(12, tenantId);
        Assert.Equal(1751347200, timestamp);
        Assert.Equal("photo.jpg", fileName);
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
