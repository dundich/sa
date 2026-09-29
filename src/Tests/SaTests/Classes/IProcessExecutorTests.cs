using Sa.Classes;
using System.Diagnostics;

namespace SaTests.Classes;

/// <summary>
/// Регрессии на <see cref="IProcessExecutor"/>: параметр <c>timeout</c> и токен отмены.
/// </summary>
public class IProcessExecutorTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static ProcessStartInfo Sleep(int seconds) => new("sleep", $"{seconds}")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    /// <summary>
    /// ArgumentList, а не строка Arguments: на Unix .NET режет строку по пробелам без учёта
    /// кавычек, и «-c 'echo ...'» распадается на токен «'echo» — shell получает нераскрытую
    /// кавычку и падает с «Unterminated quoted string» (exit 2).
    /// </summary>
    private static ProcessStartInfo Shell(string script)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd") { ArgumentList = { "/c", script } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", script } };

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        return startInfo;
    }

    private static async Task DrainAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[16];
        while (await stream.ReadAsync(buf, ct) > 0) { }
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutIsHonoured_ThrowsProcessTimeoutException()
    {
        var sw = Stopwatch.StartNew();

        // Раньше условие создания CTS было `timeout.Value == TimeSpan.Zero`, то есть любой
        // осмысленный timeout игнорировался и вызов висел до естественного выхода процесса.
        var ex = await Assert.ThrowsAsync<ProcessTimeoutException>(
            () => IProcessExecutor.Default.ExecuteAsync(Sleep(30), _ => { }, _ => { }, TimeSpan.FromSeconds(1), TestToken));

        Assert.InRange(sw.Elapsed.TotalSeconds, 0.5, 10);
        Assert.Contains("00:00:01", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_NullTimeout_DoesNotTimeOut()
    {
        var lines = new List<string>();

        var exitCode = await IProcessExecutor.Default.ExecuteAsync(
            Shell("echo hello"), line => lines.Add(line.TrimEnd('\r', '\n')), _ => { }, timeout: null, cancellationToken: TestToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("hello", lines);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroTimeout_MeansNoTimeout()
    {
        // null и TimeSpan.Zero обязаны трактоваться одинаково — «без таймаута».
        Assert.Equal(0, await IProcessExecutor.Default.ExecuteAsync(
            Shell("echo hello"), _ => { }, _ => { }, TimeSpan.Zero, TestToken));
    }

    [Fact]
    public async Task ExecuteAsync_CallerCancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();

        // Task.WhenAll не прерывается на отмену (только на fault), а стрим-таски завершаются
        // лишь на EOF процесса. Зависший процесс пайпы не закроет, поэтому отмена уезжала
        // в ожидание естественной смерти процесса — измерено 30 секунд вместо 200 мс.
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => IProcessExecutor.Default.ExecuteAsync(Sleep(30), _ => { }, _ => { }, timeout: null, cancellationToken: cts.Token));

        Assert.InRange(sw.Elapsed.TotalSeconds, 0.1, 10);
        Assert.True(cts.IsCancellationRequested);
        // ProcessTimeoutException не наследует OperationCanceledException, так что если бы
        // отмена превратилась в таймаут, ThrowsAnyAsync её бы не проглотил.
        Assert.IsNotType<ProcessTimeoutException>(ex);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutAndCallerCancellation_AreDistinguishable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Токен вызывающего жив, отменяется только наш таймаут.
        await Assert.ThrowsAsync<ProcessTimeoutException>(
            () => IProcessExecutor.Default.ExecuteAsync(Sleep(30), _ => { }, _ => { }, TimeSpan.FromSeconds(1), cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_LargeOutputOnBothStreams_CompletesAndDrains()
    {
        // Процесс, забивающий оба перенаправленных потока: проверяем, что путь
        // «читатели ещё не дошли до EOF» не съедает таймаут и не теряет строки.
        var outCount = 0;
        var errCount = 0;

        var exitCode = await IProcessExecutor.Default.ExecuteAsync(
            Shell("for i in $(seq 1 2000); do echo out-$i; echo err-$i 1>&2; done"),
            _ => Interlocked.Increment(ref outCount),
            _ => Interlocked.Increment(ref errCount),
            TimeSpan.FromSeconds(30),
            TestToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(2000, outCount);
        Assert.Equal(2000, errCount);
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExit_ThrowsResultExceptionWithStderr()
    {
        var ex = await Assert.ThrowsAsync<ProcessExecutionResultException>(
            () => IProcessExecutor.Default.ExecuteWithResultAsync(Shell("echo out-line & echo err-line 1>&2 & exit 3"), cancellationToken: TestToken));

        Assert.Equal(3, ex.Result.ExitCode);
        Assert.Contains("err-line", ex.Result.StandardError);
        Assert.Contains("out-line", ex.Result.StandardOutput);
    }

    [Fact]
    public async Task ExecuteStdOutAsync_TimeoutIsHonoured_ThrowsProcessTimeoutException()
    {
        var sw = Stopwatch.StartNew();

        // Процесс молчит, поэтому DrainAsync висит в ReadAsync до отмены linked-токена.
        await Assert.ThrowsAsync<ProcessTimeoutException>(
            () => IProcessExecutor.Default.ExecuteStdOutAsync(
                Sleep(30), new MemoryStream(), DrainAsync, TimeSpan.FromSeconds(1), TestToken));

        Assert.InRange(sw.Elapsed.TotalSeconds, 0.5, 10);
    }

    [Fact]
    public async Task ExecuteStdOutAsync_CallerCancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => IProcessExecutor.Default.ExecuteStdOutAsync(
                Sleep(30), new MemoryStream(), DrainAsync, timeout: null, cancellationToken: cts.Token));

        Assert.InRange(sw.Elapsed.TotalSeconds, 0.1, 10);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyCancelledToken_ThrowsImmediately()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => IProcessExecutor.Default.ExecuteAsync(Sleep(30), _ => { }, _ => { }, timeout: null, cancellationToken: cts.Token));

        Assert.InRange(sw.Elapsed.TotalSeconds, 0, 5);
    }
}
