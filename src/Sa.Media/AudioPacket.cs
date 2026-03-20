namespace Sa.Media;

/// <summary>
/// Сырые или сконвертированные сэмплы
/// </summary>
/// <param name="ChannelId">номер канала</param>
/// <param name="Sample">сэпмл</param>
/// <param name="Position">Абсолютное cмещение сэпмла с учетом header size</param>
/// <param name="IsEof">флаг последний сэпмл</param>
public sealed record AudioPacket(
    int ChannelId,
    ReadOnlyMemory<byte> Sample,
    long Position,
    bool IsEof);
