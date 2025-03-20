using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Sa.Extensions;

public static class JsonExtensions
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
