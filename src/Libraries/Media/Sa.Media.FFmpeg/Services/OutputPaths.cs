namespace Sa.Media.FFmpeg.Services;

/// <summary>
/// Разрешение имени выходного файла относительно <see cref="FFMpegOptions.WritableDirectory"/>.
/// <para>
/// Одна реализация на всех: <see cref="FFMpegExecutor"/> и <see cref="PcmS16LeChannelManipulator"/>
/// обязаны понимать «относительное имя» одинаково. Пока <c>SplitAsync</c> разрешал путь сам,
/// моно-ветка (через <see cref="FFMpegExecutor"/>) писала в <c>WritableDirectory</c>, а
/// стерео-ветка (через raw-executor) — в текущий каталог процесса: файлы оказывались в разных
/// местах, и возвращённые пути могли не существовать.
/// </para>
/// </summary>
internal static class OutputPaths
{
    /// <summary>
    /// Возвращает путь к выходному файлу. Имя без каталога кладётся в
    /// <paramref name="writableDirectory"/> (если он задан), явный каталог сохраняется как есть.
    /// </summary>
    public static string Resolve(string outputFileName, string? writableDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFileName);

        if (string.IsNullOrWhiteSpace(writableDirectory)
            || !string.IsNullOrEmpty(Path.GetDirectoryName(outputFileName)))
        {
            return outputFileName;
        }

        return Path.Combine(writableDirectory, outputFileName);
    }
}
