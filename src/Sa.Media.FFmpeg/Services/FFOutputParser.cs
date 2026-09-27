namespace Sa.Media.FFmpeg.Services;

static class FFOutputParser
{
    /// <summary>
    /// Разбирает вывод <c>ffprobe -show_entries stream=channels,sample_rate</c>.
    /// <para>
    /// ВНИМАНИЕ: вызывать только с <c>-select_streams a:0</c> (см. FFProbeExecutor). Без него
    /// ffprobe печатает блок на каждый поток, и «последний выигрывает» молча возвращал параметры
    /// чужой дорожки. Параметры, которых нет в выводе, остаются null — раньше они были 0, и
    /// «0 каналов» неотличим от «каналы неизвестны».
    /// </para>
    /// </summary>
    public static (int? channels, int? sampleRate) ParseChannelsAndSampleRate(string str)
    {
        ArgumentNullException.ThrowIfNull(str);

        int? sampleRate = null;
        int? channels = null;

        foreach (ReadOnlySpan<char> line in str.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.IsEmpty) continue;

            var equalIndex = trimmed.IndexOf('=');
            if (equalIndex == -1) continue;

            var key = trimmed[..equalIndex].Trim();
            var value = trimmed[(equalIndex + 1)..].Trim();
            if (value.IsEmpty) continue;

            if (key.SequenceEqual("sample_rate".AsSpan()))
                sampleRate = value.StrToInt();
            else if (key.SequenceEqual("channels".AsSpan()))
                channels = value.StrToInt();
        }

        return (channels, sampleRate);
    }
}
