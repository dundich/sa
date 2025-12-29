namespace Sa.Extensions;

internal static class GuidExtensions
{
    public static Guid ToMinGuidV7(this Guid guid)
    {
        if (guid.Version != 7)
        {
            throw new ArgumentException("GUID must be version 7", nameof(guid));
        }

        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes, bigEndian: true, out _);


        bytes[6] = (byte)(bytes[6] & 0xF0);
        bytes[7] = 0x00;
        bytes[8] = (byte)(bytes[8] & 0xC0);
        bytes[9..16].Clear();

        return new Guid(bytes, bigEndian: true);
    }
}
