using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sa.Partitional.PostgreSql.Classes;

/// <summary>
/// A discriminated union that represents either a <see cref="ChoiceStr"/> (string) or a <see cref="ChoiceNum"/> (64-bit integer).
/// Used throughout Sa.Partitional.PostgreSql for partition key values that may be either text labels or numeric identifiers.
/// </summary>
/// <example>
/// <code>
/// StrOrNum val = 10;
/// StrOrNum val_1 = "hello";
/// string v = val.Match(
///    onChoiceNum: item => $"long: {item}",
///    onChoiceStr: item => $"string: {item}"
/// );
/// </code>
/// </example>
/// <seealso href="https://github.com/salvois/DiscriminatedOnions/blob/master/DiscriminatedOnions/Choice.cs"/>
[JsonConverter(typeof(StrOrNumConverter))]
public abstract record StrOrNum
{
    /// <summary>
    /// String variant of the discriminated union.
    /// </summary>
    /// <param name="Item">The string value.</param>
    public record ChoiceStr(string Item) : StrOrNum
    {
        public override string ToString() => Item;
    }

    /// <summary>
    /// Numeric variant of the discriminated union.
    /// </summary>
    /// <param name="Item">The <see cref="long"/> value.</param>
    public record ChoiceNum(long Item) : StrOrNum
    {
        // Invariant on purpose: the rendered value ends up in table names and SQL literals, and a
        // culture with a different negative sign (U+2212) or digits would corrupt both.
        public override string ToString() => Item.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Dispatches to either <paramref name="onChoiceStr"/> or <paramref name="onChoiceNum"/> depending on the active variant.
    /// </summary>
    /// <typeparam name="U">The return type shared by both branches.</typeparam>
    /// <param name="onChoiceStr">Callback invoked when this is a <see cref="ChoiceStr"/>.</param>
    /// <param name="onChoiceNum">Callback invoked when this is a <see cref="ChoiceNum"/>.</param>
    /// <returns>The result of the invoked callback.</returns>
    public U Match<U>(Func<string, U> onChoiceStr, Func<long, U> onChoiceNum)
        => Match(onChoiceStr, onChoiceNum, this);


    /// <summary>
    /// Implicitly converts a <see cref="string"/> to a <see cref="StrOrNum"/> wrapping <see cref="ChoiceStr"/>.
    /// </summary>
    public static implicit operator StrOrNum(string item) => new ChoiceStr(item);

    /// <summary>
    /// Implicitly converts an <see cref="int"/> to a <see cref="StrOrNum"/> wrapping <see cref="ChoiceNum"/>.
    /// </summary>
    public static implicit operator StrOrNum(int item) => new ChoiceNum(item);

    /// <summary>
    /// Implicitly converts a <see cref="long"/> to a <see cref="StrOrNum"/> wrapping <see cref="ChoiceNum"/>.
    /// </summary>
    public static implicit operator StrOrNum(long item) => new ChoiceNum(item);

    /// <summary>
    /// Implicitly converts a <see cref="short"/> to a <see cref="StrOrNum"/> wrapping <see cref="ChoiceNum"/>.
    /// </summary>
    public static implicit operator StrOrNum(short item) => new ChoiceNum(item);

    /// <summary>
    /// Explicitly extracts the underlying <see cref="string"/> from a <see cref="ChoiceStr"/>,
    /// or parses a <see cref="ChoiceNum"/> back to its string representation.
    /// </summary>
    public static explicit operator string(StrOrNum choice) => choice.Match(c1 => c1, c2 => c2.ToString());

    /// <summary>
    /// Explicitly extracts the underlying <see cref="long"/> from a <see cref="ChoiceNum"/>,
    /// or attempts to parse a <see cref="ChoiceStr"/> as a number (returns 0 on failure).
    /// </summary>
    public static explicit operator long(StrOrNum choice) => choice.Match(c1 => StrToLong(c1) ?? 0, c2 => c2);

    /// <summary>
    /// Explicitly extracts the underlying <see cref="int"/> from a <see cref="ChoiceNum"/>,
    /// or attempts to parse a <see cref="ChoiceStr"/> as a number (returns 0 on failure).
    /// </summary>
    public static explicit operator int(StrOrNum choice) => choice.Match(c1 => StrToInt(c1) ?? 0, c2 => (int)c2);

    /// <summary>
    /// Explicitly extracts the underlying <see cref="short"/> from a <see cref="ChoiceNum"/>,
    /// or attempts to parse a <see cref="ChoiceStr"/> as a number (returns 0 on failure).
    /// </summary>
    public static explicit operator short(StrOrNum choice) => choice.Match(c1 => StrToShort(c1) ?? 0, c2 => (short)c2);

    private static U Match<U>(Func<string, U> onChoiceStr, Func<long, U> onChoiceNum, StrOrNum choice)
    {
        U result = choice switch
        {
            ChoiceStr c => onChoiceStr(c.Item),
            ChoiceNum c => onChoiceNum(c.Item),
            _ => throw new ArgumentOutOfRangeException(nameof(choice))
        };

        return result;
    }

    /// <summary>
    /// Returns the contained value as a human-readable string.
    /// </summary>
    public override string ToString() => Match(str => str, num => num.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Returns a formatted serialisation string prefixed with the variant kind
    /// (<c>s:&lt;value&gt;</c> for string, <c>n:&lt;value&gt;</c> for number).
    /// This format is used by <see cref="FromFmtStr"/> and the JSON converter.
    /// </summary>
    /// <remarks>
    /// A string payload is escaped so the separator that joins several values into one field
    /// (<c>,</c> in the cache table's <c>part_values</c>) can never appear unescaped inside it, and a
    /// literal backslash is doubled to keep the escape reversible. Numbers need neither - the
    /// invariant format contains neither character.
    /// </remarks>
    public string ToFmtString()
        => Match(str => $"s:{EscapeFmt(str)}", num => $"n:{num.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Parses a formatted string produced by <see cref="ToFmtString"/> back into a <see cref="StrOrNum"/>.
    /// Strings without a prefix are treated as <see cref="ChoiceStr"/>.
    /// </summary>
    /// <param name="fmtInput">The formatted input string.</param>
    /// <returns>A <see cref="StrOrNum"/> instance matching the original value.</returns>
    /// <remarks>A malformed <c>n:</c> number falls back to <see cref="ChoiceStr"/>, keeping the raw
    /// text, instead of silently becoming the number 0. Callers that need to detect the corruption
    /// use <see cref="TryFromFmtStr"/>.</remarks>
    public static StrOrNum FromFmtStr(string? fmtInput)
        => TryFromFmtStr(fmtInput, out StrOrNum? result) ? result : new ChoiceStr(fmtInput ?? string.Empty);

    /// <summary>
    /// Strict counterpart of <see cref="FromFmtStr"/>: reports whether <paramref name="fmtInput"/>
    /// is a well-formed formatted value instead of falling back.
    /// </summary>
    /// <param name="fmtInput">The formatted input string.</param>
    /// <param name="result">The parsed value, or <see langword="null"/> when the input is malformed.</param>
    /// <returns><see langword="true"/> when <paramref name="fmtInput"/> could be parsed.</returns>
    public static bool TryFromFmtStr(string? fmtInput, [NotNullWhen(true)] out StrOrNum? result)
    {
        result = null;

        if (fmtInput is null) return false;

        if (fmtInput.StartsWith("s:", StringComparison.Ordinal))
        {
            result = new ChoiceStr(UnescapeFmt(fmtInput.AsSpan()[2..]));
            return true;
        }

        if (fmtInput.StartsWith("n:", StringComparison.Ordinal))
        {
            if (StrToLong(fmtInput.AsSpan()[2..]) is not long num) return false;

            result = new ChoiceNum(num);
            return true;
        }

        result = new ChoiceStr(fmtInput);
        return true;
    }

    private StrOrNum() { }

    /// <summary>
    /// Escapes the two characters that carry meaning in the formatted protocol: the backslash that
    /// introduces an escape, and the comma that separates several values inside one field.
    /// </summary>
    private static string EscapeFmt(string value)
    {
        // Fast path: most partition values contain neither character.
        if (!value.Contains(',', StringComparison.Ordinal) && !value.Contains('\\', StringComparison.Ordinal)) return value;

        StringBuilder sb = new(value.Length + 8);
        foreach (char c in value)
        {
            if (c is ',' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reverses <see cref="EscapeFmt"/> in a single pass. Two sequential replacements would be
    /// wrong: the <c>\\</c> produced for a literal backslash would be re-read as an escape, so
    /// <c>a\,b</c> would decode to <c>a\b</c>.
    /// </summary>
    private static string UnescapeFmt(ReadOnlySpan<char> value)
    {
        int escapeIndex = value.IndexOf('\\');
        if (escapeIndex < 0) return value.ToString();

        StringBuilder sb = new(value.Length);
        sb.Append(value[..escapeIndex]);

        for (int i = escapeIndex; i < value.Length; i++)
        {
            char c = value[i];

            // A backslash is an escape only when something follows it that it can escape;
            // a trailing lone backslash is a literal one.
            if (c == '\\' && i + 1 < value.Length)
            {
                char next = value[i + 1];
                if (next is ',' or '\\')
                {
                    sb.Append(next);
                    i++;
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reports whether the character at <paramref name="index"/> is preceded by an odd number of
    /// backslashes, i.e. whether it is escaped and therefore not a value separator.
    /// </summary>
    internal static bool IsEscapedAt(ReadOnlySpan<char> text, int index)
    {
        int backslashes = 0;
        for (int i = index - 1; i >= 0 && text[i] == '\\'; i--) backslashes++;
        return (backslashes & 1) == 1;
    }

    private static int? StrToInt(ReadOnlySpan<char> str) => int.TryParse(str, CultureInfo.InvariantCulture, out int result) ? result : null;
    private static short? StrToShort(ReadOnlySpan<char> str) => short.TryParse(str, CultureInfo.InvariantCulture, out short result) ? result : null;

    /// <summary>
    /// Safely parses a <see cref="ReadOnlySpan{T}"/> of characters into a <see cref="long"/>.
    /// Uses <see cref="CultureInfo.InvariantCulture"/> to avoid culture-dependent parsing issues.
    /// </summary>
    /// <param name="str">The character span to parse.</param>
    /// <returns>The parsed <see cref="long"/>, or <c>null</c> if parsing fails.</returns>
    public static long? StrToLong(ReadOnlySpan<char> str) => long.TryParse(str, CultureInfo.InvariantCulture, out long result) ? result : null;
}



/// <summary>
/// Serialises <see cref="StrOrNum"/> to/from JSON using the formatted <c>s:/n:</c> protocol.
/// </summary>
public class StrOrNumConverter : JsonConverter<StrOrNum>
{
    /// <summary>
    /// Reads a JSON string and deserialises it into a <see cref="StrOrNum"/> via <see cref="StrOrNum.FromFmtStr"/>.
    /// </summary>
    public override StrOrNum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Null))
        {
            throw new JsonException(
                $"Expected a JSON string in the 's:'/'n:' format for {typeToConvert.Name}, got {reader.TokenType}.");
        }

        return StrOrNum.FromFmtStr(reader.GetString());
    }

    /// <summary>
    /// Writes a <see cref="StrOrNum"/> as a JSON string using <see cref="StrOrNum.ToFmtString"/>.
    /// </summary>
    public override void Write(Utf8JsonWriter writer, StrOrNum value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToFmtString());
}
