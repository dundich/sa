using Sa.Extensions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sa.Classes;

///<summary>
///StrOrNum
/// <example>
/// <code>
/// StrOrNum val = 10; 
/// StrOrNum val_1 = "привет";
/// string v = val.Match(
///    onChoiceNum: item => $"long: {item}",
///    onChoiceStr: item => $"string: {item}"
/// );
/// </code>
/// </example>
/// <seealso cref="https://github.com/salvois/DiscriminatedOnions/blob/master/DiscriminatedOnions/Choice.cs"/>
/// </summary>
[JsonConverter(typeof(StrOrNumConverter))]
public abstract record StrOrNum
{
    public record ChoiceStr(string Item) : StrOrNum
    {
        public override string ToString() => $"s:{Item}";
    }

    public record ChoiceNum(long Item) : StrOrNum
    {
        public override string ToString() => $"n:{Item}";
    }

    public U Match<U>(Func<string, U> onChoiceStr, Func<long, U> onChoiceNum)
        => Match(onChoiceStr, onChoiceNum, this);


    public static implicit operator StrOrNum(string item) => new ChoiceStr(item);

    public static implicit operator StrOrNum(int item) => new ChoiceNum(item);
    public static implicit operator StrOrNum(long item) => new ChoiceNum(item);
    public static implicit operator StrOrNum(short item) => new ChoiceNum(item);

    public static explicit operator string(StrOrNum choice) => choice.Match(c1 => c1, c2 => c2.ToString());
    public static explicit operator long(StrOrNum choice) => choice.Match(c1 => c1.StrToLong() ?? 0, c2 => c2);
    public static explicit operator int(StrOrNum choice) => choice.Match(c1 => c1.StrToInt() ?? 0, c2 => (int)c2);
    public static explicit operator short(StrOrNum choice) => choice.Match(c1 => c1.StrToShort() ?? 0, c2 => (short)c2);

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

    public override string ToString() => Match(str => $"s:{str}", num => $"n:{num}");

    public static StrOrNum Parse(string? input)
    {
        if (string.IsNullOrEmpty(input)) return new ChoiceStr(string.Empty);

        if (input.StartsWith("s:", StringComparison.Ordinal))
        {
            return new ChoiceStr(input[2..]);
        }
        else if (input.StartsWith("n:", StringComparison.Ordinal))
        {
            return new ChoiceNum(input[2..].StrToLong() ?? 0);
        }
        else
        {
            return new ChoiceStr(input);
        }
    }

    private StrOrNum() { }
}



public class StrOrNumConverter : JsonConverter<StrOrNum>
{
    public override StrOrNum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
      => StrOrNum.Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, StrOrNum value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Match(s => $"s:{s}", n => $"n:{n}"));
}
