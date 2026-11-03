namespace Sa.Media.FFmpeg.Services;

/// <summary>
/// Единственный способ собрать командную строку FFmpeg в этой библиотеке.
/// <para>
/// Раньше на каждый вызов приходился свой почти-идентичный метод: у части стояли флаги
/// «побайтового» WAV, у части нет, перезапись проверялась по-разному, пути к файлам квотились
/// вручную и одинаково неэкранированно. Здесь всё это задаётся один раз, поэтому разъехаться
/// вызовы больше не могут.
/// </para>
/// <para>
/// <c>-nostdin</c> входит в <see cref="Constants.CleanBannerFlags"/>: без него FFmpeg при
/// <c>isOverwrite: false</c> печатает <c>Overwrite? [y/N]</c> и блокируется на stdin, который у
/// нас перенаправлен, — процесс висел бы до таймаута.
/// </para>
/// <para>
/// Тип — <c>ref struct</c>: командная строка собирается в пул из <c>ArrayPool</c> и не
/// аллоцирует промежуточных строк. Из этого следует ограничение, которое видно на вызовах в
/// FFMpegExecutor: <c>ToString()</c>/<c>BuildFile()</c> обязаны стоять в вызывающем коде,
/// потому что параметром <c>async</c>-метода ref struct быть не может (CS4012). Оба одноразовые.
/// </para>
/// </summary>
internal ref struct FFCmd
{
    ValueStringBuilder _b;
    string? _outputFileName;
    bool _isOverwrite;
    bool _consumed;

    public FFCmd()
    {
        _b = new ValueStringBuilder(Constants.StringBuilderInitialCapacity);
        _b.Append(Constants.CleanBannerFlags);
    }

    /// <summary>Читает <paramref name="inputFileName"/>, пишет <paramref name="outputFileName"/>.</summary>
    public static FFCmd File(string inputFileName, string outputFileName, string codec, bool isOverwrite)
    {
        FFMpegExecutor.CheckFiles(inputFileName, outputFileName);

        var cmd = new FFCmd { _outputFileName = outputFileName };
        cmd.AppendOverwrite(isOverwrite);
        cmd.Input(inputFileName);
        cmd.MapFirstAudioStream();
        cmd.Codec(codec);
        return cmd;
    }

    /// <summary>Читает stdin как <paramref name="inputFormat"/>, пишет stdout.</summary>
    public static FFCmd Pipe(string inputFormat, string codec)
    {
        FFmpegArgs.ValidateFormatName(inputFormat, nameof(inputFormat));

        var cmd = new FFCmd();
        cmd._b.Append(" -f ");
        cmd._b.Append(inputFormat);
        cmd._b.Append(" -i pipe:0 ");
        cmd.MapFirstAudioStream();
        cmd.Codec(codec);
        return cmd;
    }

    public FFCmd SampleRate(int? sampleRate)
    {
        if (sampleRate is not { } rate) return this;
        _b.Append(" -ar ");
        _b.Append(rate);
        return this;
    }

    public FFCmd Channels(ushort? channels)
    {
        if (channels is not { } count) return this;
        _b.Append(" -ac ");
        _b.Append(count);
        return this;
    }

    /// <summary>Флаги, делающие WAV воспроизводимым побайтово (без LIST/INFO, с известным data-размером).</summary>
    public FFCmd Wav() => Format("wav").Raw(Constants.CleanWavOutputFlags);

    public FFCmd Format(string muxer)
    {
        _b.Append(" -f ");
        _b.Append(muxer);
        return this;
    }

    /// <summary>Дописывает готовый кусок аргументов как есть.</summary>
    public FFCmd Raw(string args)
    {
        _b.Append(' ');
        _b.Append(args);
        return this;
    }

    /// <summary>Одноразовый: возвращает строку и возвращает буфер в пул.</summary>
    public override string ToString()
    {
        EnsureNotConsumed();
        _consumed = true;

        return Build();
    }

    /// <summary>
    /// Одноразовый: для файловых конвертаций возвращает команду вместе с выходным файлом и
    /// флагом перезаписи. <c>FFMpegExecutor.RunFileAsync</c> нужен и путь (чтобы проверить
    /// файл до и после запуска — FFmpeg 7.1 при отказе в перезаписи по <c>-n</c> выходит с
    /// кодом 0, и код возврата это не ловит), и флаг (чтобы решить, проверять ли «уже есть»).
    /// </summary>
    public (string Command, string OutputFileName, bool IsOverwrite) BuildFile()
    {
        EnsureNotConsumed();
        _consumed = true;

        if (_outputFileName is null)
            throw new InvalidOperationException(
                $"{nameof(BuildFile)}() requires a file command built by {nameof(File)}(). " +
                "This command has no output file — it writes to stdout.");

        return (Build(), _outputFileName, _isOverwrite);
    }

    void EnsureNotConsumed()
    {
        if (_consumed)
            throw new InvalidOperationException(
                $"{nameof(FFCmd)} is single-use: the command has already been built. " +
                "Call ToString() or BuildFile() exactly once, at the point where the command is handed to the executor.");
    }

    string Build()
    {
        if (_outputFileName is null)
        {
            _b.Append(" pipe:1");
        }
        else
        {
            _b.Append(' ');
            FFmpegArgs.AppendQuoted(ref _b, _outputFileName);
        }

        return _b.ToString();
    }

    void AppendOverwrite(bool isOverwrite)
    {
        // -y перезаписывает молча, -n отказывает молча. Раньше при isOverwrite: false не было
        // ни того, ни другого: FFmpeg задавал вопрос в stdin и ждал ответа, которого не было.
        _isOverwrite = isOverwrite;
        _b.Append(isOverwrite ? " -y" : " -n");
    }

    void Input(string inputFileName)
    {
        _b.Append(" -i ");
        FFmpegArgs.AppendQuoted(ref _b, inputFileName);
    }

    /// <summary>
    /// -map 0:a:0 — брать первый аудиопоток. Без него ffmpeg сам выбирает «лучший», а в
    /// многоязычных дорожках это может быть не та, что нужна, и результат зависит от кодека.
    /// </summary>
    void MapFirstAudioStream()
    {
        _b.Append(" -map 0:a:0 ");
    }

    void Codec(string codec)
    {
        _b.Append(" -c:a ");
        _b.Append(codec);
    }
}
