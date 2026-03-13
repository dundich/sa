namespace Sa.Media;

public sealed record AudioPacket(
    int ChannelId,
    ReadOnlyMemory<byte> Sample,
    long CurrentOffset,
    bool IsEof);
