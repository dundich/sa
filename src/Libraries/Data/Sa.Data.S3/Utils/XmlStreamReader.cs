using System.Runtime.CompilerServices;
using System.Xml;

namespace Sa.Data.S3.Utils;

/// <summary>
/// Streaming reader for the small S3 XML documents (list results, multipart start).
/// </summary>
/// <remarks>
/// A real <see cref="XmlReader"/> instead of the previous byte-by-byte scanner: the scanner wrote
/// into a fixed 256-char stack buffer, so a single object key longer than that threw
/// <see cref="IndexOutOfRangeException"/>, and it returned XML entities raw
/// (<c>a&amp;amp;b</c> for <c>a&amp;b</c>), which made the listed key differ from the stored one.
/// <see cref="XmlReader"/> decodes entities, never truncates and reads in chunks.
/// </remarks>
internal static class XmlStreamReader
{
    /// <summary>
    /// Creates an asynchronous, DTD-free reader over <paramref name="stream"/>. The stream stays
    /// owned by the caller: disposing the reader must not close the response stream.
    /// </summary>
    public static XmlReader CreateReader(Stream stream) => XmlReader.Create(stream, new XmlReaderSettings
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
    });

    /// <summary>
    /// Yields the text of every element with the given local name, in document order.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadValuesAsync(
        Stream stream,
        string elementName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = CreateReader(stream);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType is XmlNodeType.Element
                && string.Equals(reader.LocalName, elementName, StringComparison.Ordinal))
            {
                yield return await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Returns the text of the first element with the given local name, or an empty string when the
    /// document has no such element.
    /// </summary>
    public static async Task<string> ReadStringAsync(
        Stream stream,
        string elementName,
        CancellationToken ct = default)
    {
        await foreach (var value in ReadValuesAsync(stream, elementName, ct).ConfigureAwait(false))
        {
            return value;
        }

        return string.Empty;
    }
}
