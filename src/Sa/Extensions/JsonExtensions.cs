using System.Diagnostics;
using System.Text.Json;

namespace Sa.Extensions;

public static class JsonExtensions
{
    [DebuggerStepThrough]
    public static string ToJson<T>(this T value, JsonSerializerOptions? options = null)
    {
        return JsonSerializer.Serialize<T>(value, options);
    }


    [DebuggerStepThrough]
    public static T? FromJson<T>(this string value, JsonSerializerOptions? options = null)
    {
        return JsonSerializer.Deserialize<T>(value, options);
    }
}
