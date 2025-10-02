using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Sa.Extensions;

internal static class JsonExtensions
{
    [DebuggerStepThrough]
    [RequiresDynamicCode(JsonHttpResultTrimmerWarning.SerializationRequiresDynamicCodeMessage)]
    [RequiresUnreferencedCode(JsonHttpResultTrimmerWarning.SerializationUnreferencedCodeMessage)]
    public static string ToJson<T>(this T value, JsonSerializerOptions? options = null)
    {
        return JsonSerializer.Serialize<T>(value, options);
    }


    [DebuggerStepThrough]
    [RequiresDynamicCode(JsonHttpResultTrimmerWarning.SerializationRequiresDynamicCodeMessage)]
    [RequiresUnreferencedCode(JsonHttpResultTrimmerWarning.SerializationUnreferencedCodeMessage)]
    public static T? FromJson<T>(this string value, JsonSerializerOptions? options = null)
    {
        return JsonSerializer.Deserialize<T>(value, options);
    }
}


public static class JsonHttpResultTrimmerWarning
{
    public const string SerializationUnreferencedCodeMessage = "JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext.";
    public const string SerializationRequiresDynamicCodeMessage = "JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use the overload that takes a JsonTypeInfo or JsonSerializerContext.";
}