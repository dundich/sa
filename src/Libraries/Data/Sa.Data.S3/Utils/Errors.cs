using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Sa.Data.S3.Utils;

internal static class Errors
{
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void CantFormatToString<T>(T value)
        where T : struct
    {
        throw new FormatException($"Can't format '{value}' to string");
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Disposed()
    {
        throw new ObjectDisposedException(nameof(S3BucketClient));
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void UnexpectedResult(HttpResponseMessage response)
    {
        var reason = response.ReasonPhrase ?? response.ToString();
        var exception = new HttpRequestException($"Data has returned an unexpected result: {response.StatusCode} ({reason})");

        response.Dispose();

        throw exception;
    }
}
