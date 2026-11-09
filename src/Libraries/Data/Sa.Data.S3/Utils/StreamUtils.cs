using System.Runtime.CompilerServices;

namespace Sa.Data.S3.Utils;

internal static class StreamUtils
{
    public static async Task<int> ReadTo(this Stream stream, byte[] buffer, CancellationToken cancellation)
    {
        var length = buffer.Length;
        var offset = 0;

        int written;
        do
        {
            written = await stream
                .ReadAsync(buffer.AsMemory(offset, length), cancellation)
                .ConfigureAwait(false);

            length -= written;
            offset += written;
        }
        while (written > 0 && length > 0);

        return offset;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long? TryGetLength(this Stream stream)
    {
        try
        {
            // Zero is a real length, not "unknown": an empty stream is uploaded with a plain PUT of
            // content-length 0, exactly like an empty byte[]. Mapping 0 to null used to route it to
            // the multipart branch, which cannot complete a zero-part upload and returned false.
            return stream.Length;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
