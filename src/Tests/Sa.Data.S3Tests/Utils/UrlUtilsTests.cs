using Sa.Data.S3.Utils;

namespace Sa.Data.S3Tests.Utils;

public class UrlUtilsTests
{
    [Theory]
    [InlineData("my-bucket", null, "my-bucket")]
    [InlineData("my-bucket", "file.txt", "my-bucket/file.txt")]
    [InlineData("my-bucket", "folder/file.txt", "my-bucket/folder/file.txt")]
    [InlineData("my-bucket", "file with spaces.txt", "my-bucket/file%20with%20spaces.txt")]
    [InlineData("my-bucket", "file@name.txt", "my-bucket/file%40name.txt")]
    public void BuildFileUrl_ShouldReturnCorrectUrl(string bucket, string? fileName, string expectedUrl)
    {
        // Act
        var result = UrlUtils.BuildFileUrl(bucket, fileName);

        // Assert
        Assert.Equal(expectedUrl, result);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("folder/file.txt", "folder%2Ffile.txt")]
    [InlineData("a+b", "a%2Bb")]
    [InlineData("x=1&y=2", "x%3D1%26y%3D2")]
    [InlineData("ab+cd/ef=", "ab%2Bcd%2Fef%3D")]
    [InlineData("привет", "%D0%BF%D1%80%D0%B8%D0%B2%D0%B5%D1%82")]
    public void EncodeQueryValue_ShouldPercentEncodeBeyondTheUnreservedSet(string value, string expected)
    {
        Assert.Equal(expected, UrlUtils.EncodeQueryValue(value));
    }
}
