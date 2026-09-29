using System.Runtime.CompilerServices;

namespace Sa.Media.FFmpeg.Services;

internal sealed class FFMpegExecutor(IFFRawExecutor executor, string? writableDirectory = null) : IFFMpegExecutor
{
    // Если задан FFMpegOptions.WritableDirectory, выходной файл без каталога кладём в него:
    // текущий рабочий каталог у службы, в контейнера с read-only rootfs и в процессах
    // без заданного CWD может быть недоступен на запись. Раньше опция существовала,
    // но нигде не читалась.
    readonly string? _writableDirectory = string.IsNullOrWhiteSpace(writableDirectory) ? null : writableDirectory;

    public IFFRawExecutor Executor => executor;

    /// <summary>Разрешает имя выходного файла относительно <see cref="FFMpegOptions.WritableDirectory"/>.</summary>
    string ResolveOutput(string outputFileName)
    {
        if (_writableDirectory is null || !string.IsNullOrEmpty(Path.GetDirectoryName(outputFileName)))
            return outputFileName;

        return Path.Combine(_writableDirectory, outputFileName);
    }

    public async Task<string> GetVersion(CancellationToken cancellationToken = default)
    {
        var result = await executor.ExecuteAsync("-version", cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    public async Task<string> GetFormats(CancellationToken cancellationToken = default)
    {
        var result = await executor.ExecuteAsync("-formats", cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    public async Task<string> GetCodecs(CancellationToken cancellationToken = default)
    {
        var result = await executor.ExecuteAsync("-codecs", cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    public Task<string> ConvertToPcmS16Le(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunFileAsync(
            FFCmd.File(inputFileName, ResolveOutput(outputFileName), "pcm_s16le", isOverwrite)
                .SampleRate(outputSampleRate)
                .Channels(outputChannelCount)
                .Wav().BuildFile(),
            timeout, cancellationToken);

    public Task ConvertToPcmS16Le(
        Stream inputStream,
        string inputFormat,
        Func<Stream, CancellationToken, Task> onOutput,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunStreamAsync(
            FFCmd.Pipe(inputFormat, "pcm_s16le")
                .SampleRate(outputSampleRate)
                .Channels(outputChannelCount)
                .Wav().ToString(),
            inputStream, onOutput, timeout, cancellationToken);

    public Task<string> ConvertToMp3(
        string inputFileName,
        string outputFileName,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunFileAsync(
            FFCmd.File(inputFileName, ResolveOutput(outputFileName), "libmp3lame", isOverwrite)
                .Raw("-ar 16000 -b:a 128k")
                .Format("mp3").BuildFile(),
            timeout, cancellationToken);

    public Task<string> ConvertToOgg(
        string inputFileName,
        string outputFileName,
        bool isLibopus = false,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunFileAsync(
            FFCmd.File(inputFileName, ResolveOutput(outputFileName), isLibopus ? "libopus" : "libvorbis", isOverwrite)
                .Format("ogg").BuildFile(),
            timeout, cancellationToken);

    public Task<string> ConvertToPcmS16LePreservingFormat(
        string inputFileName,
        string outputFileName,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => ConvertToPcmS16Le(
            inputFileName,
            outputFileName,
            outputSampleRate: null,
            outputChannelCount: null,
            isOverwrite,
            timeout,
            cancellationToken);

    public Task<string> ConvertToPcmS16LeRaw(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunFileAsync(
            FFCmd.File(inputFileName, ResolveOutput(outputFileName), "pcm_s16le", isOverwrite)
                .SampleRate(outputSampleRate)
                .Channels(outputChannelCount)
                .Format("s16le").BuildFile(),
            timeout, cancellationToken);

    public Task ConvertToPcmS16LeRaw(
        Stream inputStream,
        string inputFormat,
        Func<Stream, CancellationToken, Task> onOutput,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunStreamAsync(
            FFCmd.Pipe(inputFormat, "pcm_s16le")
                .SampleRate(outputSampleRate)
                .Channels(outputChannelCount)
                .Format("s16le").ToString(),
            inputStream, onOutput, timeout, cancellationToken);

    public Task<string> ConvertToPcmS32Le(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunFileAsync(
            FFCmd.File(inputFileName, ResolveOutput(outputFileName), "pcm_s32le", isOverwrite)
                .SampleRate(outputSampleRate)
                .Channels(outputChannelCount)
                .Wav().BuildFile(),
            timeout, cancellationToken);

    public Task<string> ConvertToPcmF32Le(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunFileAsync(
            FFCmd.File(inputFileName, ResolveOutput(outputFileName), "pcm_f32le", isOverwrite)
                .SampleRate(outputSampleRate)
                .Channels(outputChannelCount)
                .Wav().BuildFile(),
            timeout, cancellationToken);

    public Task<string> ConvertToPcmF32LeRaw(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunFileAsync(
            FFCmd.File(inputFileName, ResolveOutput(outputFileName), "pcm_f32le", isOverwrite)
                .SampleRate(outputSampleRate)
                .Channels(outputChannelCount)
                .Format("f32le").BuildFile(),
            timeout, cancellationToken);

    public Task ConvertToPcmF32LeRaw(
        Stream inputStream,
        string inputFormat,
        Func<Stream, CancellationToken, Task> onOutput,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunStreamAsync(
            FFCmd.Pipe(inputFormat, "pcm_f32le")
                .SampleRate(outputSampleRate)
                .Channels(outputChannelCount)
                .Format("f32le").ToString(),
            inputStream, onOutput, timeout, cancellationToken);

    /// <summary>
    /// Общий путь для всех файловых конвертаций.
    /// <para>
    /// <c>throwOnError: true</c> — обязателен. Раньше здесь стоял <c>false</c>, и ошибка FFmpeg
    /// (нет файла на входе, неизвестный кодек, занятый output) молча возвращалась вызывающему как
    /// пустая строка: метод завершался «успешно», не создав ни одного файла.
    /// </para>
    /// <para>
    /// Но и кода возврата недостаточно: FFmpeg 7.1 при отказе перезаписывать файл по <c>-n</c>
    /// печатает «File '...' already exists. Exiting.» в stderr и <b>выходит с кодом 0</b>
    /// (проверено на 7.1.5). Поэтому успех дополнительно подтверждается наличием выходного
    /// файла, а отказ перезаписывать ловится до запуска процесса.
    /// </para>
    /// <para>
    /// Аргумент — результат <c>FFCmd.BuildFile()</c>, а не готовая строка: путь к выходному
    /// файлу и флаг перезаписи нужны для проверок, и держать их во второй переменной на
    /// вызовах было бы повторением той же информации в двух местах.
    /// </para>
    /// </summary>
    async Task<string> RunFileAsync(
        (string Command, string OutputFileName, bool IsOverwrite) file,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        // Проверяем сами, потому что на это поведение FFmpeg код возврата не смотрит.
        if (!file.IsOverwrite && File.Exists(file.OutputFileName))
            throw new IOException(
                $"Output file already exists and isOverwrite is false: '{file.OutputFileName}'. " +
                "Pass isOverwrite: true to replace it.");

        var result = await executor.ExecuteAsync(
            file.Command,
            throwOnError: true,
            timeout: timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!File.Exists(file.OutputFileName))
            throw new ProcessExecutionException(
                0,
                $"FFmpeg reported success but did not create '{file.OutputFileName}'. " +
                $"Most likely the bundled build lacks the requested encoder.{Environment.NewLine}{result.StandardError}");

        return result.StandardError;
    }

    async Task RunStreamAsync(
        string cmd,
        Stream inputStream,
        Func<Stream, CancellationToken, Task> onOutput,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
        => await executor.ExecuteStdOutAsync(
            cmd,
            inputStream,
            onOutput,
            timeout: timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CheckFiles(string inputFileName, string outputFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFileName);

        // Сравнение путей должно учитывать регистр той ФС, на которой идёт конвертация.
        // OrdinalIgnoreCase на Linux ловил несуществующую коллизию: /tmp/A.wav и /tmp/a.wav —
        // разные файлы, и отказывать в конвертации было незачем.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var input = Path.GetFullPath(inputFileName);
        var output = Path.GetFullPath(outputFileName);

        if (string.Equals(input, output, comparison))
            throw new ArgumentException(
                $"Input and output must be different files, but both are '{input}'. " +
                "FFmpeg would truncate the input before reading it.",
                nameof(outputFileName));

        // Раньше несуществующий вход доходил до FFmpeg, который падал с exit code 1, а этот код
        // молча проглатывал ошибку. Проверяем здесь — с понятным сообщением.
        if (!File.Exists(input))
            throw new FileNotFoundException($"Input file not found: '{input}'.", input);
    }
}
