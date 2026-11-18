using Sa.Data.S3.Utils;
using System.Text;

namespace Sa.Data.S3Tests.Utils;

/// <summary>
/// Разбор XML-ответов S3: прежний сканер писал в фиксированный буфер на 256 символов и возвращал
/// сущности сырыми, поэтому проверяем обе регрессии отдельно от сервера (у SeaweedFS свой лимит
/// длины ключа, и через него длинный ключ не создать).
/// </summary>
public class XmlStreamReaderShould
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static MemoryStream Xml(string value) => new(Encoding.UTF8.GetBytes(value));

    [Fact]
    public async Task ReadValueLongerThanTheOldFixedBuffer()
    {
        var key = new string('k', 300);

        using var stream = Xml(
            $"<ListBucketResult><Contents><Key>{key}</Key></Contents></ListBucketResult>");

        Assert.Equal(key, await XmlStreamReader.ReadStringAsync(stream, "Key", Token));
    }

    [Fact]
    public async Task DecodeXmlEntitiesBackToTheOriginalKey()
    {
        const string expected = "a&b<c>d\"e'f.txt";

        using var stream = Xml("<R><Key>a&amp;b&lt;c&gt;d&quot;e&apos;f.txt</Key></R>");

        Assert.Equal(expected, await XmlStreamReader.ReadStringAsync(stream, "Key", Token));
    }

    [Fact]
    public async Task EnumerateEveryMatchingElement()
    {
        using var stream = Xml(
            "<ListBucketResult>" +
            "<Contents><Key>a.txt</Key></Contents>" +
            "<Contents><Key>b.txt</Key></Contents>" +
            "<KeyCount>2</KeyCount>" +
            "</ListBucketResult>");

        var keys = new List<string>();
        await foreach (var key in XmlStreamReader.ReadValuesAsync(stream, "Key", Token))
        {
            keys.Add(key);
        }

        // KeyCount не должен попасть в выборку: имя элемента сравнивается целиком.
        Assert.Equal(["a.txt", "b.txt"], keys);
    }

    [Fact]
    public async Task ReturnEmptyStringWhenTheElementIsAbsent()
    {
        using var stream = Xml("<ListBucketResult></ListBucketResult>");

        Assert.Empty(await XmlStreamReader.ReadStringAsync(stream, "UploadId", Token));
    }
}
