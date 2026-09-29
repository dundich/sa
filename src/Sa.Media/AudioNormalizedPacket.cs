namespace Sa.Media;

/// <summary>
/// Нормализованные double-сэмплы
/// </summary>
/// <param name="ChannelId">номер канала</param>
/// <param name="Sample">сэпмл</param>
/// <param name="Position">Абсолютное cмещение сэпмла с учетом header size</param>
/// <param name="IsEof">флаг последний сэпмл</param>
public readonly record struct AudioNormalizedPacket(
    int ChannelId,
    double Sample,
    long Position,
    bool IsEof);
