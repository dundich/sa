using Sa.Data.S3.Utils;

namespace Sa.Data.S3Tests.Utils;

public class QueryParameterTests
{

    [Fact]
    public void EmptyQuery()
    {
        var builder = new ValueStringBuilder(stackalloc char[512]);
        UrlUtils.AppendCanonicalQueryParameters(ref builder, null);
        Assert.Empty(builder.ToString());
        UrlUtils.AppendCanonicalQueryParameters(ref builder, "");
        Assert.Empty(builder.ToString());
        UrlUtils.AppendCanonicalQueryParameters(ref builder, "?");
        Assert.Empty(builder.ToString());

        builder.Dispose();
    }

    [Fact]
    public void SingleParameter()
    {
        var builder = new ValueStringBuilder(stackalloc char[512]);
        UrlUtils.AppendCanonicalQueryParameters(ref builder, "?key=value");
        Assert.Equal("key=value", builder.ToString());

        builder.Dispose();
    }

    [Fact]
    public void MultipleParameters()
    {
        var builder = new ValueStringBuilder(stackalloc char[512]);
        UrlUtils.AppendCanonicalQueryParameters(ref builder, "?key1=value1&key2=value2");
        Assert.Equal("key1=value1&key2=value2", builder.ToString());

        builder.Dispose();
    }

    [Fact]
    public void ParameterWithWhitespace()
    {
        var builder = new ValueStringBuilder(stackalloc char[512]);
        UrlUtils.AppendCanonicalQueryParameters(ref builder, "? key1 = value1 & key2 = value2 ");
        Assert.Equal("key1%20=%20value1%20&key2%20=%20value2%20", builder.ToString());

        builder.Dispose();
    }

    [Fact]
    public void ParameterWithoutValue()
    {
        var builder = new ValueStringBuilder(stackalloc char[512]);
        UrlUtils.AppendCanonicalQueryParameters(ref builder, "?key1&key2=value2");
        Assert.Equal("key1=&key2=value2", builder.ToString());

        builder.Dispose();
    }

    [Fact]
    public void ParameterWithEmptyValue()
    {
        var builder = new ValueStringBuilder(stackalloc char[512]);
        UrlUtils.AppendCanonicalQueryParameters(ref builder, "?key1=&key2=");
        Assert.Equal("key1=&key2=", builder.ToString());

        builder.Dispose();
    }

    [Fact]
    public void ParameterWithSpecialCharacters()
    {
        var builder = new ValueStringBuilder(stackalloc char[512]);
        UrlUtils.AppendCanonicalQueryParameters(ref builder, "?key1=value%20with%20spaces&key2=value%26with%26ampersands");
        Assert.Equal("key1=value%20with%20spaces&key2=value%26with%26ampersands", builder.ToString());

        builder.Dispose();
    }

    [Fact]
    public void SlashInValueIsPercentEncoded()
    {
        // SigV4 кодирует «/» в query как %2F: иначе листинг по «папке» даёт SignatureDoesNotMatch.
        var builder = new ValueStringBuilder(stackalloc char[512]);
        UrlUtils.AppendCanonicalQueryParameters(ref builder, "?prefix=folder/sub");
        Assert.Equal("prefix=folder%2Fsub", builder.ToString());

        builder.Dispose();
    }

    [Fact]
    public void ParametersAreSorted()
    {
        var builder = new ValueStringBuilder(stackalloc char[512]);
        UrlUtils.AppendCanonicalQueryParameters(ref builder, "?b=2&a=1&c=3");
        Assert.Equal("a=1&b=2&c=3", builder.ToString());

        builder.Dispose();
    }

    [Fact]
    public void UploadIdReservedCharactersRoundTrip()
    {
        // Upload id у AWS — base64: «+», «/», «=». Они обязаны пережить разбор и кодирование
        // без изменений.
        var builder = new ValueStringBuilder(stackalloc char[512]);
        UrlUtils.AppendCanonicalQueryParameters(ref builder, "?partNumber=1&uploadId=ab%2Bcd%2Fef%3D");
        Assert.Equal("partNumber=1&uploadId=ab%2Bcd%2Fef%3D", builder.ToString());

        builder.Dispose();
    }
}
