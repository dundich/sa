using Sa.Media.FFmpeg;
using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpegTests;

/// <summary>
/// Тесты, которые гоняют настоящий ffmpeg.
/// <para>
/// Здесь проверяется не состав аргументов (это делает <see cref="CommandLineTests"/> на
/// подмене), а то, что реальный процесс стартует, читает нужный файл и завершается там, где
/// должен. Именно такие проверки ловят ошибки квотирования: имя файла разбирает не наш код,
/// а .NET — и на глаз отличить «экранировано правильно» от «просто не повезло» нельзя.
/// </para>
/// </summary>
public sealed class FfmpegProcessTests
{
    static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    const string SourceWav = "./data/stereo_join.wav";

    /// <summary>Каталог, куда пишут тесты. Общий на класс: временные файлы сами удалять не нужно.</summary>
    static string WorkDir
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "sa-ffmpeg-tests");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Имя файла из символов, которые ломают наивное склеивание аргументов. Набор зависит от
    /// ОС: "\" в Windows — разделитель пути, а кавычки в имени на Windows допустимы, но
    /// приводят к другим граблям в cmd.exe, которых здесь нет (UseShellExecute = false).
    /// </summary>
    static IEnumerable<string> HostileFileNames()
    {
        yield return "with space.wav";
        yield return "with'apostrophe.wav";
        yield return "with;semicolon.wav";
        yield return "with&ampersand.wav";
        yield return "with(parens).wav";
        yield return "with$dollar.wav";
        yield return "with=equals.wav";
        yield return "двойной пробел  внутри.wav";

        if (!OperatingSystem.IsWindows())
        {
            yield return "с\\обратным слэшем.wav";
            yield return "с\"кавычкой\".wav";
            yield return "с'одинарной кавычкой'.wav";
        }
    }

    [Fact]
    public async Task ConvertToPcmS16Le_ReadsFileWhoseNameLooksLikeCommandLineInjection()
    {
        var mpeg = IFFMpegExecutor.Default;

        foreach (var name in HostileFileNames())
        {
            var input = Path.Combine(WorkDir, name);
            File.Copy(SourceWav, input, overwrite: true);
            var output = Path.Combine(WorkDir, $"out_{Path.GetFileNameWithoutExtension(name)}.wav");

            await mpeg.ConvertToPcmS16Le(input, output, cancellationToken: CancellationToken);

            Assert.True(File.Exists(output), $"No output produced for input named '{name}'.");
            Assert.True(new FileInfo(output).Length > 44, $"Output for '{name}' is empty.");
        }
    }

    [Fact]
    public async Task ConvertToPcmS16Le_ThrowsWhenInputIsMissing()
    {
        var mpeg = IFFMpegExecutor.Default;
        var missing = Path.Combine(WorkDir, "нет-такого-файла.wav");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => mpeg.ConvertToPcmS16Le(missing, Path.Combine(WorkDir, "o.wav"), cancellationToken: CancellationToken));
    }

    [Fact]
    public async Task ConvertToPcmS16Le_DoesNotHangWhenOverwriteIsForbidden()
    {
        // Регрессия: при isOverwrite: false не передавалось ни -y, ни -n. FFmpeg печатал
        // «File exists. Overwrite? [y/N]» в stdin и ждал ответа, которого не будет, —
        // до таймаута.
        //
        // Короткий таймаут нужен для другой откатной ветки: FFmpeg 7.1, даже с правильным
        // -n, при отказе в перезаписи печатает в stderr и выходит с кодом 0 (проверено на
        // 7.1.5), — код возврата это не ловит, и только проверка «файл существует» до запуска
        // (и после) делает поведение предсказуемым.
        var mpeg = IFFMpegExecutor.Default;
        var input = Path.Combine(WorkDir, "no-overwrite-in.wav");
        var output = Path.Combine(WorkDir, "no-overwrite-out.wav");
        File.Copy(SourceWav, input, overwrite: true);
        File.Copy(SourceWav, output, overwrite: true);

        var before = File.ReadAllBytes(output);

        await Assert.ThrowsAsync<IOException>(
            () => mpeg.ConvertToPcmS16Le(input, output, isOverwrite: false, timeout: TimeSpan.FromSeconds(20), cancellationToken: CancellationToken));

        // Файл остался нетронутым: не одно байт не переписано.
        Assert.Equal(before, File.ReadAllBytes(output));
    }

    [Fact]
    public async Task ConvertToPcmS16Le_OverwritesWhenAllowed()
    {
        var mpeg = IFFMpegExecutor.Default;
        var input = Path.Combine(WorkDir, "overwrite-in.wav");
        var output = Path.Combine(WorkDir, "overwrite-out.wav");
        File.Copy(SourceWav, input, overwrite: true);
        File.WriteAllBytes(output, new byte[1024]);

        await mpeg.ConvertToPcmS16Le(input, output, isOverwrite: true, cancellationToken: CancellationToken);

        Assert.NotEqual(1024, new FileInfo(output).Length);
    }

    [Fact]
    public async Task FileAndStreamConversionsProduceIdenticalAudioBytes()
    {
        // Один и тот же вход, два пути: файл на диске и поток на stdin. Аудио обязан совпасть
        // побайтово — иначе «посчитать хеш аудио» зависит от того, откуда взяли файл.
        //
        // Сравнивается data-чанк, а не весь файл: FFmpeg, пишущий WAV в pipe, не знает
        // итоговый размер заранее и кладёт в RIFF-заголовок 0xFFFFFFFF, а файл-выход пишет
        // реальный размер. Само аудио в обоих случаях идентично.
        var mpeg = IFFMpegExecutor.Default;

        var fromFile = Path.Combine(WorkDir, "determinism-file.wav");
        await mpeg.ConvertToPcmS16Le(SourceWav, fromFile, cancellationToken: CancellationToken);

        var fromStream = new MemoryStream();
        await using (var input = File.OpenRead(SourceWav))
        {
            await mpeg.ConvertToPcmS16Le(
                input,
                "wav",
                (stream, ct) => stream.CopyToAsync(fromStream, ct),
                cancellationToken: CancellationToken);
        }

        Assert.Equal(
            ExtractWavDataPayload(File.ReadAllBytes(fromFile)),
            ExtractWavDataPayload(fromStream.ToArray()));
    }

    [Fact]
    public async Task ConvertToPcmS16Le_IsByteDeterministicAcrossRuns()
    {
        // Bit-exact-флаги (-bitexact -fflags +bitexact -map_metadata -1 -write_bext 0):
        // одна и та же команда, запущенная дважды, обязана дать побайтово идентичные файлы.
        // Иначе сравнение «одинакового аудио» по хешу между запусками бессмысленно.
        var mpeg = IFFMpegExecutor.Default;

        var first = Path.Combine(WorkDir, "determinism-run1.wav");
        var second = Path.Combine(WorkDir, "determinism-run2.wav");

        await mpeg.ConvertToPcmS16Le(SourceWav, first, cancellationToken: CancellationToken);
        await mpeg.ConvertToPcmS16Le(SourceWav, second, cancellationToken: CancellationToken);

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    /// <summary>
    /// Возвращает payload data-чанка WAV: всё после 8-байтового заголовка «data», до конца файла.
    /// <para>
    /// Заявленный в чанке размер не доверяется: в pipe-выходе FFmpeg пишет там 0xFFFFFFFF, а
    /// data-чанк в нашем bit-exact WAV — последний (LIST/INFO/BEXT не пишутся), так что «до
    /// конца файла» верно и для файл-выхода с реальным размером.
    /// </para>
    /// </summary>
    static byte[] ExtractWavDataPayload(byte[] wav)
    {
        if (wav.Length < 44)
            throw new InvalidDataException("Not a WAV file: too short.");

        if (!wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a RIFF/WAVE file.");

        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            if (wav.AsSpan(pos, 4).SequenceEqual("data"u8))
                return wav[(pos + 8)..];

            var chunkSize = BitConverter.ToInt32(wav, pos + 4);
            pos += 8 + (chunkSize > 0 ? chunkSize : wav.Length - pos - 8);
        }

        throw new InvalidDataException("WAV file has no data chunk.");
    }

    [Fact]
    public async Task ConvertToPcmS16Le_LeavesTheCallersStreamOpen()
    {
        // Регрессия: внутри стоял 'await using', и повторное использование того же потока
        // падало с ObjectDisposedException. Теперь поток остаётся в ведении вызывающего.
        var mpeg = IFFMpegExecutor.Default;
        using var input = File.OpenRead(SourceWav);

        await mpeg.ConvertToPcmS16Le(
            input,
            "wav",
            (stream, ct) => stream.CopyToAsync(Stream.Null, ct),
            cancellationToken: CancellationToken);

        Assert.True(input.CanRead, "The caller's stream was closed by the executor.");

        // И осталось пригодным для повторного использования. Курсор теперь в конце файла —
        // вернуть его в начало обязанность вызывающего, как и для любого Read-стрима.
        input.Position = 0;
        var second = new MemoryStream();
        await mpeg.ConvertToPcmS16Le(
            input,
            "wav",
            (stream, ct) => stream.CopyToAsync(second, ct),
            cancellationToken: CancellationToken);

        Assert.NotEmpty(second.ToArray());
    }

    [Fact]
    public async Task ConvertToMp3_ProducesPlayableFile()
    {
        // Регрессия: для Windows-сборки ffmpeg кодировщики mp3/opus/vorbis не были собраны, а
        // проверка по ОС молча подставляла другой кодек. Теперь несовместимый кодек должен
        // падать — этот тест падает сам, если вернуть то молчаливое поведение.
        //
        // Win-x64 payload в репозитории кросс-компилирован до фикса кодеков: если в сборке
        // нет libmp3lame, тест честно пропускается (команда называет энкодер явно, и
        // «несовместимо» в этой ситуации — ожидаемое поведение, а не баг). Как только
        // payload пересоберут, тест снова выполняется.
        await CodecProbe.RequireEncoderAvailable("libmp3lame", CancellationToken);

        var mpeg = IFFMpegExecutor.Default;
        var output = Path.Combine(WorkDir, "check.mp3");

        await mpeg.ConvertToMp3(SourceWav, output, cancellationToken: CancellationToken);

        Assert.True(new FileInfo(output).Length > 1024);

        var (channels, sampleRate) = await IFFProbeExecutor.Default.GetChannelsAndSampleRate(output, CancellationToken);
        Assert.Equal(2, channels);
        Assert.Equal(sampleRate, 16000);
    }
}
