namespace Sa.Data.S3.Fixture;

public static class S3FixtureHelper
{
    public const string StreamContentType = "application/octet-stream";

    public const int DefaultByteArraySize = 1 * 1024 * 1024; // 7Mb

    public static string GetRandomFileName() => Path.GetRandomFileName();

    public static string GetRandomFileNameWithoutExtension() => Path.GetFileNameWithoutExtension(GetRandomFileName());

    public static byte[] GetByteArray(int size = DefaultByteArraySize)
    {
        var random = Random.Shared;
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)random.Next();
        }

        return bytes;
    }

    public static MemoryStream GetByteStream(int size = DefaultByteArraySize)
    {
        return new MemoryStream(GetByteArray(size));
    }

    public static MemoryStream GetEmptyByteStream(long? size = null)
    {
        return size.HasValue
            ? new MemoryStream(new byte[(int)size])
            : new MemoryStream();
    }
}
