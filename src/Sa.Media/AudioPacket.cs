namespace Sa.Media;

/// <summary>
/// Сырые или сконвертированные сэмплы
/// </summary>
/// <param name="ChannelId">номер канала</param>
/// <param name="Sample">сэмпл</param>
/// <param name="Position">
/// Байтовое смещение начала кадра от начала файла (включая заголовок); для всех каналов
/// одного кадра одинаковое.
/// </param>
/// <param name="IsEof">признак последнего кадра в выбранном диапазоне</param>
public readonly record struct AudioPacket(
    int ChannelId,
    ReadOnlyMemory<byte> Sample,
    long Position,
    bool IsEof);
