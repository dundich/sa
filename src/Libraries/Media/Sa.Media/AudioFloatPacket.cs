namespace Sa.Media;

/// <summary>
/// Нормализованные float-сэмплы
/// </summary>
/// <param name="ChannelId">номер канала</param>
/// <param name="Sample">сэмпл в диапазоне [-1.0, 1.0]</param>
/// <param name="Position">
/// Байтовое смещение начала кадра от начала файла (включая заголовок); для всех каналов
/// одного кадра одинаковое.
/// </param>
/// <param name="IsEof">признак последнего кадра в выбранном диапазоне</param>
public readonly record struct AudioFloatPacket(
    int ChannelId,
    float Sample,
    long Position,
    bool IsEof);
