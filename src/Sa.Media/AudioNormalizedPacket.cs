namespace Sa.Media;

public sealed record AudioNormalizedPacket(
    int ChannelId,
    double Sample,
    long CurrentOffset,
    bool IsEof);
