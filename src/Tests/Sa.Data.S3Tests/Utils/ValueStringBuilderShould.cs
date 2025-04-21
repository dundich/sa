using Sa.Data.S3.Utils;

namespace Sa.Data.S3Tests.Utils;

public class ValueStringBuilderShould
{
    [Fact]
    public void Grow()
    {
        const int stringLength = 256;
        var chars = Enumerable.Range(0, stringLength).Select(i => (char)i);

        var builder = new ValueStringBuilder(stackalloc char[64]);
        foreach (var c in chars)
        {
            builder.Append(c);
        }

        Assert.Equal(stringLength, builder.Length);
        builder.Dispose();

    }

    [Fact]
    public void NotCreateEmptyString()
    {
        var builder = new ValueStringBuilder(stackalloc char[64]);
        Assert.Empty(builder.ToString());
        builder.Dispose();
    }

    [Fact]
    public void RemoveLastCorrectly()
    {
        var builder = new ValueStringBuilder(stackalloc char[64]);
        builder.RemoveLast();

        Assert.True(builder.Length > -1);
        builder.Dispose();
    }
}
