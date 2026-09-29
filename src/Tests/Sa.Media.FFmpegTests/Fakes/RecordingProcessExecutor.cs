using System.Diagnostics;
using System.Text;
using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpegTests.Fakes;

/// <summary>
/// Подмена <see cref="IProcessExecutor"/>, которая записывает командные строки и возвращает
/// заготовленный ответ, ничего не запуская.
/// <para>
/// Нужна, чтобы проверять состав аргументов FFmpeg без реального процесса. Это единственный
/// способ отличить «флаг есть» от «флага нет, и тест этого не замечает»: запуск настоящего
/// ffmpeg на обоих вариантах завершился бы одинаково успешно.
/// </para>
/// </summary>
internal sealed class RecordingProcessExecutor : IProcessExecutor
{
    readonly List<string> _commands = [];

    /// <summary>Код возврата, которым «процесс» ответит.</summary>
    public int ExitCode { get; set; }

    /// <summary>Что процесс напечатал бы в stdout.</summary>
    public string StandardOutput { get; set; } = "";

    /// <summary>Что процесс напечатал бы в stderr.</summary>
    public string StandardError { get; set; } = "";

    /// <summary>Все записанные аргументы, по одному на вызов.</summary>
    public IReadOnlyList<string> Commands => _commands;

    /// <summary>Аргументы последнего вызова. Бросает, если вызовов не было.</summary>
    public string LastCommand => _commands.Count > 0
        ? _commands[^1]
        : throw new InvalidOperationException("Process was never started.");

    public Task<int> ExecuteAsync(
        ProcessStartInfo startInfo,
        Action<string>? outputDataReceived = null,
        Action<string>? errorDataReceived = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _commands.Add(startInfo.Arguments);

        // Успешный ffmpeg с файловым выходом создаёт выходной файл — RunFileAsync проверяет
        // его наличие после запуска (FFmpeg 7.1 при отказе в перезаписи выходит с кодом 0,
        // не написав ничего, и код возврата нельзя доверять). Подделка повторяет это, иначе
        // каждый файловый тест упрётся в проверку «файл не создан».
        if (ExitCode == 0)
            CreateOutputFileIfFileCommand(startInfo);

        if (outputDataReceived != null)
            foreach (var line in StandardOutput.Split('\n'))
                outputDataReceived(line);

        if (errorDataReceived != null)
            foreach (var line in StandardError.Split('\n'))
                errorDataReceived(line);

        return Task.FromResult(ExitCode);
    }

    static void CreateOutputFileIfFileCommand(ProcessStartInfo startInfo)
    {
        // Только ffmpeg: в команде ffprobe последним аргументом идёт входной файл,
        // и «создать его» означало бы затереть фикстуру.
        if (!Path.GetFileNameWithoutExtension(startInfo.FileName).Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            return;

        // В файловой команде ffmpeg последним аргументом идёт выход; в потоковой — pipe:1.
        var last = FFmpegArgsTests.SplitArguments(startInfo.Arguments).LastOrDefault();
        if (last is null || last.StartsWith("pipe:", StringComparison.Ordinal))
            return;

        var dir = Path.GetDirectoryName(Path.GetFullPath(last));
        if (dir is not null)
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(last, [0x52, 0x49, 0x46, 0x46]);
    }

    public async Task ExecuteStdOutAsync(
        ProcessStartInfo startInfo,
        Stream inputStream,
        Func<Stream, CancellationToken, Task> onOutput,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _commands.Add(startInfo.Arguments);

        // Перекачиваем stdin, как это делает настоящий исполнитель, чтобы поток вызывающего
        // действительно читался — иначе тест на «поток не закрыт» ничего не проверял бы.
        await inputStream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);

        using var buffer = new MemoryStream(Encoding.UTF8.GetBytes(StandardOutput));
        await onOutput(buffer, cancellationToken).ConfigureAwait(false);

        if (ExitCode != 0)
            throw new ProcessExecutionException(
                ExitCode,
                $"Process failed (exit={ExitCode}): {startInfo.Arguments}{Environment.NewLine}{StandardError}");
    }
}
