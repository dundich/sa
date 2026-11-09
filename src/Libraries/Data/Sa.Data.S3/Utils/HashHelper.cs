using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Sa.Data.S3.Utils;

internal static class HashHelper
{
    public static readonly string EmptyPayloadHash = Sha256ToHex(string.Empty);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetPayloadHash(ReadOnlySpan<byte> data)
    {
        Span<byte> hashBuffer = stackalloc byte[32];
        return SHA256.TryHashData(data, hashBuffer, out _)
            ? ToHex(hashBuffer)
            : string.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetPayloadHash(string data)
    {
        return Sha256ToHex(data);
    }

    /// <summary>
    /// Base64 MD5 of the payload — the <c>Content-MD5</c> header S3 requires on
    /// <c>POST ?delete</c> (DeleteObjects). SHA-256 is used for the signature next to it; MD5 here
    /// is not a security choice, it is the wire format the API mandates.
    /// </summary>
#pragma warning disable CA5351 // MD5 is mandated by the S3 DeleteObjects request format.
    public static string GetMd5Base64(ReadOnlySpan<byte> data)
    {
        Span<byte> hashBuffer = stackalloc byte[16];
        return MD5.TryHashData(data, hashBuffer, out _)
            ? Convert.ToBase64String(hashBuffer)
            : string.Empty;
    }
#pragma warning restore CA5351


    public static string ToHex(ReadOnlySpan<byte> data)
    {
        Span<char> buffer = stackalloc char[2];
        using var builder = new ValueStringBuilder(stackalloc char[64]);

        foreach (ref readonly var element in data)
        {
            builder.Append(buffer[..StringUtils.FormatX2(ref buffer, element)]);
        }

        return builder.Flush();
    }


    private static string Sha256ToHex(ReadOnlySpan<char> value)
    {
        var arrayPool = DefaultArrayPool.Instance;

        var count = Encoding.UTF8.GetByteCount(value);

        var byteBuffer = arrayPool.Rent<byte>(count);

        var encoded = Encoding.UTF8.GetBytes(value, byteBuffer);
        Span<byte> hashBuffer = stackalloc byte[32];
        var result = SHA256.TryHashData(byteBuffer.AsSpan(0, encoded), hashBuffer, out var written)
            ? ToHex(hashBuffer[..written])
            : string.Empty;

        arrayPool.Return(byteBuffer);

        return result;
    }
}
