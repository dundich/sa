using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

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

    /// <summary>
    /// Disposes the response and throws <see cref="HttpRequestException"/> carrying the status code
    /// and the (bounded) S3 error body.
    /// </summary>
    /// <remarks>
    /// The body is the only part that explains a failure: <c>SignatureDoesNotMatch</c> or
    /// <c>NoSuchBucket</c> live in the XML, while the reason phrase is a bare "Forbidden". Reading
    /// it is asynchronous because the client always sends with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>. The method always throws — the
    /// <c>return</c> after its call sites exists only for the compiler.
    /// </remarks>
    public static async Task UnexpectedResult(HttpResponseMessage response, CancellationToken ct = default)
    {
        var status = response.StatusCode;
        var reason = response.ReasonPhrase ?? status.ToString();
        var body = await ReadErrorBody(response, ct).ConfigureAwait(false);

        response.Dispose();

        var message = $"Data has returned an unexpected result: {(int)status} {status} ({reason})";
        if (body.Length > 0)
        {
            message = $"{message}: {body}";
        }

        throw new HttpRequestException(message, inner: null, status);
    }

    private static async Task<string> ReadErrorBody(HttpResponseMessage response, CancellationToken ct)
    {
        const int maxBytes = 2048;

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            var buffer = new byte[maxBytes];
            var read = await stream
                .ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return string.Empty;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, read).Trim();
            return read == maxBytes ? $"{text}…" : text;
        }
        catch (Exception)
        {
            // Diagnostics must never mask the original failure (or a cancelled/closed response).
            return string.Empty;
        }
    }
}
