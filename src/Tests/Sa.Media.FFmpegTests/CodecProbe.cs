using Sa.Media.FFmpeg;

namespace Sa.Media.FFmpegTests;

static class CodecProbe
{
    /// <summary>
    /// Hard-fails the test if the named encoder is absent from the detected FFmpeg build
    /// (<c>ffmpeg -codecs</c>).
    /// <para>
    /// Оба бандл-пейлоада (sa/linux-x64, sa/win-x64) собираются из build/common.sh с
    /// <c>--enable-libmp3lame/--enable-libvorbis/--enable-libopus</c> и парой
    /// <c>--enable-encoder=lib*</c>, а команды библиотеки называют энкодеры явно
    /// (<c>-c:a libmp3lame</c> / <c>-c:a libvorbis</c>). Payload, потерявший энкодер, обязан
    /// валить сбор зелёным тестом — тихий skip пустил бы регрессию сквозь CI. Для локальных
    /// запусков против произвольного системного FFmpeg используйте
    /// <see cref="EnsureEncoderAvailable"/>.
    /// </para>
    /// <para>
    /// Формат вывода <c>-codecs</c> (ffmpeg 7.x): строка описывает канонический кодек, а
    /// реализации перечислены в описании —
    /// « DEAIL. mp3  MP3 (…) (decoders: mp3float mp3) (encoders: libmp3lame)».
    /// Поэтому имя ищется по токену строки после снятия скобок/двоеточий/запятых:
    /// «libmp3lame)» → «libmp3lame». Это же совпадение работает в старых сборках, где
    /// у каждой реализации своя строка. Семейное имя «vorbis» (нативный *декодер*)
    /// не проходит как «libvorbis» (*энкодер*) — а именно последний называет команда.
    /// </para>
    /// </summary>
    public static async Task RequireEncoderAvailable(string encoderName, CancellationToken token)
        => await CheckEncoder(encoderName, token, skipWhenMissing: false);

    /// <summary>
    /// Аналог <see cref="RequireEncoderAvailable"/>, но <c>Assert.Skip</c> вместо падения —
    /// для локальных запусков против произвольного системного FFmpeg, который может
    /// не содержать энкодер.
    /// </summary>
    public static async Task EnsureEncoderAvailable(string encoderName, CancellationToken token)
        => await CheckEncoder(encoderName, token, skipWhenMissing: true);

    private static async Task CheckEncoder(string encoderName, CancellationToken token, bool skipWhenMissing)
    {
        var codecs = await IFFMpegExecutor.Default.GetCodecs(token);

        if (!Tokens(codecs).Contains(encoderName, StringComparer.Ordinal))
        {
            var message = $"Encoder '{encoderName}' is not in the detected FFmpeg build — " +
                          "rebuild the payload (build/common.sh carries the codec flags)." +
                          $"{Environment.NewLine}{await DescribeDetectedBuildAsync(token)}";

            if (skipWhenMissing)
                Assert.Skip(message);
            else
                Assert.Fail(message);
        }
    }

    /// <summary>
    /// Слова из вывода <c>ffmpeg -codecs</c>. <see cref="string.Trim()"/> обязателен перед
    /// снятием скобок: на Windows строки приходят с <c>\r</c>, и токен в конце строки
    /// (<c>libvorbis\r</c>) не совпадал ни с одним именем энкодера.
    /// </summary>
    private static IEnumerable<string> Tokens(string codecs) =>
        codecs
            .Split('\n')
            .SelectMany(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(part => part.Trim().Trim('(', ')', ':', ','));

    /// <summary>
    /// Что именно тест увидел: путь к бинарю, его версия и список внешних библиотек
    /// (<c>lib*</c>), найденных в сборке. Без этого «энкодера нет» не отличить от «взят
    /// не тот ffmpeg» — а Windows-лег уже один раз упал именно так, и по сообщению
    /// невозможно было понять, какой бинарь опрошен.
    /// </summary>
    private static async Task<string> DescribeDetectedBuildAsync(CancellationToken token)
    {
        var executor = IFFMpegExecutor.Default;

        var version = (await executor.GetVersion(token))
            .Split('\n')
            .FirstOrDefault(line => line.Contains("version", StringComparison.OrdinalIgnoreCase))
            ?.Trim() ?? "(version unknown)";

        var external = string.Join(", ", Tokens(await executor.GetCodecs(token))
            .Where(part => part.StartsWith("lib", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(part => part, StringComparer.Ordinal));

        return $"Detected: {version}{Environment.NewLine}" +
               $"Executable: {executor.Executor.ExecutablePath}{Environment.NewLine}" +
               $"External encoders: {external}";
    }
}
