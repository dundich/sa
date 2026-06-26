using System.Globalization;
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
        public override string ToString() => $"{Item}";
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
    public override string ToString() => Match(str => str, num => $"{num}");

    /// <summary>
    /// Returns a formatted serialisation string prefixed with the variant kind
    /// (<c>s:&lt;value&gt;</c> for string, <c>n:&lt;value&gt;</c> for number).
    /// This format is used by <see cref="FromFmtStr"/> and the JSON converter.
    /// </summary>
    public string ToFmtString() => Match(str => $"s:{str}", num => $"n:{num}");

    /// <summary>
    /// Parses a formatted string produced by <see cref="ToFmtString"/> back into a <see cref="StrOrNum"/>.
    /// Strings without a prefix are treated as <see cref="ChoiceStr"/>.
    /// </summary>
    /// <param name="fmtInput">The formatted input string.</param>
    /// <returns>A <see cref="StrOrNum"/> instance matching the original value.</returns>
    public static StrOrNum FromFmtStr(string? fmtInput)
    {
        if (string.IsNullOrEmpty(fmtInput)) return new ChoiceStr(string.Empty);

        if (fmtInput.StartsWith("s:", StringComparison.Ordinal))
        {
            return new ChoiceStr(fmtInput[2..]);
        }
        else if (fmtInput.StartsWith("n:", StringComparison.Ordinal))
        {
            return new ChoiceNum(StrToLong(fmtInput.AsSpan()[2..]) ?? 0);
        }
        else
        {
            return new ChoiceStr(fmtInput);
        }
    }

    private StrOrNum() { }

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
      => StrOrNum.FromFmtStr(reader.GetString());

    /// <summary>
    /// Writes a <see cref="StrOrNum"/> as a JSON string using <see cref="StrOrNum.ToFmtString"/>.
    /// </summary>
    public override void Write(Utf8JsonWriter writer, StrOrNum value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToFmtString());
}
