using System.Collections.Frozen;
using System.Text;

namespace Sa.Data.S3.Utils;

internal static class UrlUtils
{
    /// <summary>
    /// Characters that stay as-is in an object key (a path): RFC 3986 unreserved plus the segment
    /// separator <c>/</c>, so a nested key stays nested instead of turning into <c>%2F</c>.
    /// </summary>
    private static readonly FrozenSet<char> PathSafeCharacters =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.~/".ToFrozenSet();

    /// <summary>
    /// Characters that stay as-is in a canonical query value: the RFC 3986 unreserved set only.
    /// SigV4 percent-encodes everything else in the query, and <c>/</c> in particular MUST become
    /// <c>%2F</c> — the server re-encodes the received query the same way before comparing
    /// signatures, so a raw <c>/</c> produces <c>SignatureDoesNotMatch</c>.
    /// </summary>
    private static readonly FrozenSet<char> QuerySafeCharacters =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.~".ToFrozenSet();

    /// <summary>
    /// Builds the canonical query string: names and values encoded by the query rules, parameters
    /// sorted by name (then value) as SigV4 requires.
    /// </summary>
    public static void AppendCanonicalQueryParameters(scoped ref ValueStringBuilder builder, string? query)
    {
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return;
        }

        List<(string Name, string Value)> parameters = [];

        int scanIndex = query[0] == '?' ? 1 : 0;
        int textLength = query.Length;

        while (scanIndex < textLength)
        {
            int delimiter = query.IndexOf('&', scanIndex);
            if (delimiter == -1)
            {
                delimiter = textLength;
            }

            int equalIndex = query.IndexOf('=', scanIndex);
            if (equalIndex == -1 || equalIndex > delimiter)
            {
                equalIndex = delimiter; // No value, treat as empty
            }

            // Trim whitespace for the name
            while (scanIndex < equalIndex && char.IsWhiteSpace(query[scanIndex]))
            {
                scanIndex++;
            }

            var name = UrlUtils.EncodeQueryValue(
                UrlUtils.UnescapeString(query.AsSpan(scanIndex, equalIndex - scanIndex)));

            var value = equalIndex < delimiter
                ? UrlUtils.EncodeQueryValue(
                    UrlUtils.UnescapeString(query.AsSpan(equalIndex + 1, delimiter - equalIndex - 1)))
                : string.Empty;

            parameters.Add((name, value));
            scanIndex = delimiter + 1;
        }

        // SigV4: parameters sorted by name, equal names by value. Sorting is done on the encoded
        // form because that is what the signature is computed over.
        parameters.Sort(static (left, right) =>
        {
            var byName = string.CompareOrdinal(left.Name, right.Name);
            return byName != 0 ? byName : string.CompareOrdinal(left.Value, right.Value);
        });

        for (var index = 0; index < parameters.Count; index++)
        {
            if (index > 0)
            {
                builder.Append('&');
            }

            builder.Append(parameters[index].Name);
            builder.Append('=');
            builder.Append(parameters[index].Value);
        }
    }

    /// <summary>
    /// Appends an object key (a path) with path encoding: <c>/</c> stays unencoded.
    /// </summary>
    public static bool AppendEncodedName(scoped ref ValueStringBuilder builder, ReadOnlySpan<char> name)
        => AppendEncoded(ref builder, name, PathSafeCharacters);

    /// <summary>
    /// Encodes a value for a query string. Unlike a path, <c>/</c>, <c>+</c> and everything else
    /// outside the unreserved set are percent-encoded.
    /// </summary>
    public static string EncodeQueryValue(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var builder = new ValueStringBuilder(stackalloc char[value.Length <= 256 ? value.Length : 256]);
        var encoded = AppendEncodedQueryValue(ref builder, value);

        if (encoded)
        {
            return builder.Flush();
        }

        builder.Dispose();
        return value;
    }

    public static bool AppendEncodedQueryValue(scoped ref ValueStringBuilder builder, ReadOnlySpan<char> value)
        => AppendEncoded(ref builder, value, QuerySafeCharacters);

    public static string BuildFileUrl(string bucket, string? fileName = null)
    {
        var url = new ValueStringBuilder(stackalloc char[512]);
        url.Append(bucket);

        // ReSharper disable once InvertIf
        if (!string.IsNullOrEmpty(fileName))
        {
            url.Append('/');
            AppendEncodedName(ref url, fileName);
        }

        return url.Flush();
    }


    public static string UnescapeString(ReadOnlySpan<char> query)
    {
        using var data = new ValueStringBuilder(stackalloc char[query.Length]);
        foreach (var ch in query)
        {
            data.Append(ch is '+' ? ' ' : ch);
        }

        return Uri.UnescapeDataString(data.Flush());
    }


    public static string UrlEncodeName(string fileName)
    {
        var builder = new ValueStringBuilder(stackalloc char[fileName.Length]);
        var encoded = AppendEncodedName(ref builder, fileName);

        if (encoded)
        {
            return builder.Flush();
        }

        // Ничего кодировать не пришлось — отдаём исходную строку, но буфер всё равно освобождаем.
        builder.Dispose();
        return fileName;
    }

    private static bool AppendEncoded(
        scoped ref ValueStringBuilder builder,
        ReadOnlySpan<char> value,
        FrozenSet<char> safeCharacters)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        var arrayPool = DefaultArrayPool.Instance;
        var count = Encoding.UTF8.GetByteCount(value);

        var byteBuffer = arrayPool.Rent<byte>(count);

        Span<char> charBuffer = stackalloc char[2];
        Span<char> upperBuffer = stackalloc char[2];

        try
        {
            var encoded = Encoding.UTF8.GetBytes(value, byteBuffer);
            var span = byteBuffer.AsSpan(0, encoded);
            var hasEncoded = false;

            foreach (var element in span)
            {
                var symbol = (char)element;
                if (safeCharacters.Contains(symbol))
                {
                    builder.Append(symbol);
                }
                else
                {
                    builder.Append('%');

                    StringUtils.FormatX2(ref charBuffer, symbol);
                    MemoryExtensions.ToUpperInvariant(charBuffer, upperBuffer);
                    builder.Append(upperBuffer);

                    hasEncoded = true;
                }
            }

            return hasEncoded;
        }
        finally
        {
            arrayPool.Return(byteBuffer);
        }
    }
}
