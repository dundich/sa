namespace Sa.Serialization.Converter;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Converter that infers object to primitive types. See
/// <see href="https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/converters-how-to?pivots=dotnet-7-0#deserialize-inferred-types-to-object-properties"/>
/// </summary>
public class ObjectToInferredTypesConverter : JsonConverter<object>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReadValue(ref reader, options);


    private static object? ReadValue(ref Utf8JsonReader reader, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number when reader.TryGetInt64(out var l) => l,
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.String when reader.TryGetDateTime(out var datetime) => datetime,
            JsonTokenType.String => reader.GetString()!,
            JsonTokenType.StartArray => ParseList(ref reader, options),
            _ => JsonDocument.ParseValue(ref reader).RootElement.Clone()
        };

    private static List<object?> ParseList(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        List<object?> list = [];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            list.Add(ReadValue(ref reader, options));
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, object objectToWrite, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, objectToWrite, objectToWrite.GetType(), options);
}