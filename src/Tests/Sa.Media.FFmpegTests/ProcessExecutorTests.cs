using System.Diagnostics;
using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpegTests;

/// <summary>
/// <see cref="ProcessExecutor"/> — настоящий процесс, настоящая отмена, настоящий таймаут.
/// <para>
/// Ключевая регрессия: таймаут собирался через <c>WhenAll(wait, readers)</c>, а читатели
/// завершаются только когда процесс закрывает пайпы — зависший процесс (то, что таймаут
/// и должен поймать) закроет их никогда. 2-секундный таймаут срывался через 30 секунд, на
/// естественной смерти процесса.
/// </para>
/// </summary>
public sealed class ProcessExecutorTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    /// <summary>
    /// Долгоживущий процесс, который ничего не пишет в stdout/stderr и сам по себе не
    /// завершается до ~30 секунд: на Unix — <c>/bin/sleep</c>, на Windows — <c>ping</c>.
    /// ArgumentList вместо Arguments: аргументы уходят в argv напрямую, без парсинга
    /// кавычек .NET — здесь и в целом это единственный предсказуемый способ.
    /// </summary>
    static ProcessStartInfo LongRunningProcess()
    {
        var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "ping" : "/bin/sleep")
        {
            UseShellExecute = false
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("31");
            startInfo.ArgumentList.Add("127.0.0.1");
        }
        else
        {
            startInfo.ArgumentList.Add("30");
        }

        return startInfo;
    }

    [Fact]
    public async Task OwnTimeout_FiresAtTheDeadlineNotAtProcessDeath()
    {
        var sw = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<ProcessTimeoutException>(
            () => IProcessExecutor.Default.ExecuteWithResultAsync(
                LongRunningProcess(),
                timeout: TimeSpan.FromSeconds(2),
                cancellationToken: TestToken));

        // Таймаут срабатывает около 2 секунд, а не около 30 (смерть sleep/ping).
        // Верхняя граница 10 c — с запасом на teardown (kill + ожидание) на медленной машине.
        Assert.InRange(sw.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10));

        // Команда попала в сообщение — диагностика таймаута не «голый» текст.
        Assert.Contains("sleep", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellation_IsOperationCanceledExceptionNotTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => IProcessExecutor.Default.ExecuteAsync(
                LongRunningProcess(),
                outputDataReceived: _ => { },
                errorDataReceived: _ => { },
                timeout: null,
                cancellationToken: cts.Token));

        // Отменял именно вызывающий код: собственный таймаут в этом тесте не участвует.
        // (ProcessTimeoutException не наследует OperationCanceledException, так что если бы
        // отмена превратилась в таймаут, ThrowsAnyAsync не проглотил бы его.)
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task ShortLivedProcess_CompletesWithItsExitCode()
    {
        // ArgumentList, а не строка Arguments: на Unix .NET режет строку по пробелам без
        // учёта кавычек, и «-c 'echo ...'» распадается на токен «'echo» — shell получает
        // нераскрытую кавычку и падает с «Unterminated quoted string» (exit 2).
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd") { ArgumentList = { "/c", "echo sa-process-test" } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "echo sa-process-test" } };
        startInfo.UseShellExecute = false;

        // Строки из OutputDataReceived приходят с завершителем строки; срезаем его,
        // чтобы сравнить по содержимому.
        var lines = new List<string>();
        var exitCode = await IProcessExecutor.Default.ExecuteAsync(
            startInfo,
            outputDataReceived: line => lines.Add(line.TrimEnd('\r', '\n')),
            errorDataReceived: null,
            cancellationToken: TestToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("sa-process-test", lines);
    }
}
