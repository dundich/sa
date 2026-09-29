using Sa.Classes;
using System.Diagnostics;

namespace SaTests.Classes;

public class LockRenewerTests
{
    [Fact]
    public async Task KeepLocked_ExtendsLockUntilCancelled()
    {
        // Arrange
        var extensionCount = 0;
        var lockExpiration = TimeSpan.FromMilliseconds(50);
        using var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        async Task extendLocked(CancellationToken token)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), token); // Simulate some work
            extensionCount++;
        }

        // Act
        await using (var locker = LockRenewer.KeepLocked(
            lockExpiration, extendLocked, cancellationToken: cancellationToken))
        {
            await Task.Delay(200, TestContext.Current.CancellationToken); // Give it some time to run
            await cancellationTokenSource.CancelAsync();
        }

        // Assert
        Assert.True(extensionCount > 0, "The lock should have been extended at least once.");
    }

    [Fact]
    public async Task KeepLocked_BlockImmediately_ExtendsLockImmediately()
    {
        // Arrange
        var extensionCount = 0;
        var lockExpiration = TimeSpan.FromMilliseconds(50);
        using var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        async Task extendLocked(CancellationToken token)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), token); // Simulate some work
            extensionCount++;
        }

        // Act
        await using (var locker = LockRenewer.KeepLocked(
            lockExpiration, extendLocked, blockImmediately: true, cancellationToken: cancellationToken))
        {
            await Task.Delay(100, TestContext.Current.CancellationToken); // Give it some time to run
            await cancellationTokenSource.CancelAsync();
        }

        // Assert
        Assert.True(extensionCount > 0, "The lock should have been extended immediately.");
    }

    [Fact]
    public async Task Dispose_ReleasesResources()
    {
        // Arrange
        var extensionCount = 0;
        var lockExpiration = TimeSpan.FromMilliseconds(50);
        using var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        async Task extendLocked(CancellationToken token)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), token); // Simulate some work
            Interlocked.Increment(ref extensionCount);
        }

        // Act
        var locker = LockRenewer.KeepLocked(lockExpiration, extendLocked, cancellationToken: cancellationToken);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        await locker.DisposeAsync();


        var expected = extensionCount;
        // Assert
        Assert.True(extensionCount > 0, "The lock should have been extended immediately.");

        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(expected, extensionCount);
    }


    [Fact]
    public async Task WaitForConditionAsync_ConditionTrueImmediately_ReturnsTrueImmediately()
    {
        // Arrange
        static Task<bool> Predicate(CancellationToken ct) => Task.FromResult(true);

        // Act
        var result = await LockRenewer.WaitForConditionAsync(
            predicate: Predicate,
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task WaitForConditionAsync_ConditionBecomesTrueWithinTimeout_ReturnsTrue()
    {
        // Arrange
        var startTime = DateTime.UtcNow;
        var conditionBecomesTrueAfter = TimeSpan.FromMilliseconds(250);

        Task<bool> Predicate(CancellationToken _)
        {
            var elapsed = DateTime.UtcNow - startTime;
            return Task.FromResult(elapsed >= conditionBecomesTrueAfter);
        }

        var sw = Stopwatch.StartNew();

        // Act
        var result = await LockRenewer.WaitForConditionAsync(
            predicate: Predicate,
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken
        );

        sw.Stop();

        // Assert
        Assert.True(result);
        Assert.InRange(sw.Elapsed.TotalMilliseconds, 250, 400); // Roughly when condition became true
    }

    [Fact]
    public async Task WaitForConditionAsync_ConditionNeverTrue_ReturnsFalseAfterTimeout()
    {
        // Arrange
        static Task<bool> Predicate(CancellationToken ct) => Task.FromResult(false);

        var pollInterval = TimeSpan.FromMilliseconds(50);
        var timeout = TimeSpan.FromMilliseconds(200);
        var sw = Stopwatch.StartNew();

        // Act
        var result = await LockRenewer.WaitForConditionAsync(
            predicate: Predicate,
            timeout: timeout,
            pollInterval: pollInterval,
            TestContext.Current.CancellationToken
        );

        sw.Stop();

        // Assert
        Assert.False(result);
        Assert.True(sw.Elapsed >= timeout, "Should wait at least until timeout");
        Assert.InRange(sw.Elapsed.TotalMilliseconds, 200, 400); // Allow some overhead
    }

    [Fact]
    public async Task WaitForConditionAsync_PollingOccursWithCorrectInterval()
    {
        // Arrange
        var callTimes = new List<DateTime>();
        var pollInterval = TimeSpan.FromMilliseconds(100);

        Task<bool> Predicate(CancellationToken ct)
        {
            callTimes.Add(DateTime.UtcNow);
            return Task.FromResult(false); // never true
        }

        // Act
        var result = await LockRenewer.WaitForConditionAsync(
            predicate: Predicate,
            timeout: TimeSpan.FromMilliseconds(350), // ~3-4 polls
            pollInterval: pollInterval,
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.False(result);
        Assert.True(callTimes.Count >= 3, $"Expected 3+ calls, got {callTimes.Count}");

        for (int i = 1; i < callTimes.Count; i++)
        {
            var interval = (callTimes[i] - callTimes[i - 1]).TotalMilliseconds;
            Assert.InRange(interval, 70, 150); // Allow some jitter due to scheduling
        }
    }

    [Fact]
    public async Task WaitForConditionAsync_CancellationTokenCancelled_ReturnsFalse()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var wasCalled = false;

        Task<bool> Predicate(CancellationToken ct)
        {
            wasCalled = true;
            return Task.FromResult(false);
        }

        // Act
        var task = LockRenewer.WaitForConditionAsync(
            predicate: Predicate,
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(100),
            cancellationToken: cts.Token
        );

        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var result = await task;

        // Assert
        Assert.False(result);
        Assert.True(wasCalled, "Predicate should have been called at least once before cancellation");
    }

    [Fact]
    public async Task WaitForConditionAsync_DefaultPollInterval_Uses10ms()
    {
        // Arrange
        var callTimes = new List<long>(); // ticks
        var timeout = TimeSpan.FromMilliseconds(150);

        Task<bool> Predicate(CancellationToken ct)
        {
            callTimes.Add(DateTime.UtcNow.Ticks);
            return Task.FromResult(false);
        }

        // Act
        await LockRenewer.WaitForConditionAsync(
            predicate: Predicate,
            timeout: timeout,
            pollInterval: null,
            TestContext.Current.CancellationToken
        );

        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(callTimes.Count > 5, "Should poll frequently with 10ms default");
        for (int i = 1; i < callTimes.Count; i++)
        {
            var intervalMs = (callTimes[i] - callTimes[i - 1]) / 10_000.0; // Ticks to ms
            Assert.InRange(intervalMs, 0, 45); // ~10ms, allow jitter
        }
    }

    // ─────────────────────────── регрессии ───────────────────────────

    [Fact]
    public async Task KeepLocked_ExtendThrows_ReportsFailureOnDispose()
    {
        // Arrange
        var extendAttempts = 0;
        using var cancellationTokenSource = new CancellationTokenSource();

        Task extendLocked(CancellationToken _)
        {
            Interlocked.Increment(ref extendAttempts);
            throw new InvalidOperationException("extend failed");
        }

        // Act
        var locker = LockRenewer.KeepLocked(
            TimeSpan.FromMilliseconds(20), extendLocked, blockImmediately: true,
            cancellationToken: cancellationTokenSource.Token);

        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Assert
        // Раньше здесь стоял Debug.WriteLine — в Release он вырезается целиком, цикл тихо умирал
        // после первого сбоя, и про истёкшую блокировку не узнавал никто.
        Assert.Equal(1, extendAttempts);
        Assert.IsType<InvalidOperationException>(locker.RenewalError);

        var ex = await Assert.ThrowsAsync<LockRenewalException>(async () => await locker.DisposeAsync());
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task KeepLocked_ExtendThrows_DoesNotKillTheProcess()
    {
        // Сбой продления — это фоновая ошибка, а не повод ронять процесс.
        using var cancellationTokenSource = new CancellationTokenSource();

        var locker = LockRenewer.KeepLocked(
            TimeSpan.FromMilliseconds(10),
            _ => throw new InvalidOperationException("boom"),
            blockImmediately: true,
            cancellationToken: cancellationTokenSource.Token);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.NotNull(locker.RenewalError);
        await cancellationTokenSource.CancelAsync();
        await Assert.ThrowsAsync<LockRenewalException>(async () => await locker.DisposeAsync());
    }

    [Fact]
    public async Task KeepLocked_CleanShutdown_DoesNotThrow()
    {
        // Arrange
        using var cancellationTokenSource = new CancellationTokenSource();

        async Task extendLocked(CancellationToken token)
        {
            await Task.Delay(5, token);
        }

        // Act
        var locker = LockRenewer.KeepLocked(
            TimeSpan.FromMilliseconds(20), extendLocked,
            blockImmediately: true, cancellationToken: cancellationTokenSource.Token);

        await Task.Delay(80, TestContext.Current.CancellationToken);
        await cancellationTokenSource.CancelAsync();
        await locker.DisposeAsync();

        // Assert — штатная отмена не считается ошибкой
        Assert.Null(locker.RenewalError);
    }

    [Fact]
    public void KeepLocked_ThrowsOnNonPositiveExpiration()
    {
        // Раньше Period <= 0 прилетал из конструктора PeriodicTimer с параметром 'period'.
        var zero = Assert.Throws<ArgumentOutOfRangeException>(
            () => LockRenewer.KeepLocked(TimeSpan.Zero, _ => Task.CompletedTask, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("lockExpiration", zero.ParamName);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => LockRenewer.KeepLocked(TimeSpan.FromMilliseconds(-1), _ => Task.CompletedTask, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void KeepLocked_ReturnedHandle_ExposesBothDisposeOverloads()
    {
        // Раньше возвращаемый тип был объявлен как IAsyncDisposable, из-за чего реализация
        // IDisposable.Dispose() была недостижима: fire-and-forget вариант никто не мог вызвать.
        var handle = LockRenewer.KeepLocked(TimeSpan.FromMinutes(5), _ => Task.CompletedTask, cancellationToken: TestContext.Current.CancellationToken);

        Assert.IsAssignableFrom<IAsyncDisposable>(handle);
        Assert.IsAssignableFrom<IDisposable>(handle);

        handle.Dispose();
    }

    [Fact]
    public async Task KeepLocked_SyncDispose_DoesNotBlockOnTheRenewalTask()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var handle = LockRenewer.KeepLocked(
            TimeSpan.FromMinutes(5),
            async token =>
            {
                started.TrySetResult();
                // Продление «зависло» и держится только на отмене — самый тяжёлый случай.
                await Task.Delay(Timeout.Infinite, token);
            },
            blockImmediately: true,
            cancellationToken: cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var sw = Stopwatch.StartNew();
        handle.Dispose(); // не должен ждать зависшее продление
        sw.Stop();

        Assert.InRange(sw.Elapsed.TotalSeconds, 0, 2);
    }

    [Fact]
    public async Task KeepLocked_SyncDispose_StopsTheRenewalLoop()
    {
        // Токен вызывающего жив — цикл обязан умереть по освобождению держателя.
        var extensionCount = 0;
        using var cts = new CancellationTokenSource();

        var handle = LockRenewer.KeepLocked(
            TimeSpan.FromMilliseconds(20),
            _ => { Interlocked.Increment(ref extensionCount); return Task.CompletedTask; },
            blockImmediately: true,
            cancellationToken: cts.Token);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        handle.Dispose();
        var afterDispose = extensionCount;

        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(afterDispose, extensionCount);
    }

    [Fact]
    public async Task KeepLocked_ExtendCancelledByItself_ReportsItSeparatelyFromShutdown()
    {
        // OCE, на который не отменён наш токен, приходит не от гашения цикла, а от самого
        // вызова продления: Npgsql бросает его из Command.Cancel, например, когда сняли
        // блокировку строки. Продления не состоялось — блокировка потеряна, — но путать это
        // с обычным сбоем продления нельзя, иначе в логе не будет видно, что произошло.
        using var cts = new CancellationTokenSource();

        var handle = LockRenewer.KeepLocked(
            TimeSpan.FromMilliseconds(20),
            _ => throw new OperationCanceledException("command cancelled"),
            blockImmediately: true,
            cancellationToken: cts.Token);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.False(cts.IsCancellationRequested, "Токен вызывающего не отменяли — отмена пришла из вызова продления.");

        var ex = await Assert.ThrowsAsync<LockRenewalException>(async () => await handle.DisposeAsync());
        Assert.IsType<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task KeepLocked_ShutdownCancellation_IsNotReportedAsAFailure()
    {
        // Отмена нашего же токена — штатное завершение, даже если extendLocked бросает OCE
        // из-за переданного ему токена. Иначе каждое нормальное освобождение держателя
        // выглядело бы как потеря блокировки.
        using var cts = new CancellationTokenSource();

        async Task extendLocked(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
        }

        var handle = LockRenewer.KeepLocked(
            TimeSpan.FromMilliseconds(20), extendLocked,
            blockImmediately: true, cancellationToken: cts.Token);

        await Task.Delay(80, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await handle.DisposeAsync();

        Assert.Null(handle.RenewalError);
    }

    [Fact]
    public async Task KeepLocked_DisposeAsyncTwice_ReportsTheFailureBothTimes()
    {
        // DisposeAsync освобождает источники отмены, поэтому второе освобождение обязано
        // узнать, что они уже освобождены, — иначе вместо потери блокировки вылезает
        // ObjectDisposedException из Cancel.
        using var cts = new CancellationTokenSource();

        var handle = LockRenewer.KeepLocked(
            TimeSpan.FromMilliseconds(20),
            _ => throw new InvalidOperationException("extend failed"),
            blockImmediately: true,
            cancellationToken: cts.Token);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<LockRenewalException>(async () => await handle.DisposeAsync());
        await Assert.ThrowsAsync<LockRenewalException>(async () => await handle.DisposeAsync());
    }

    [Fact]
    public async Task KeepLocked_SyncDisposeAfterAsyncDispose_IsANoOp()
    {
        // Синхронное освобождение не должно трогать источники отмены, уже освобождённые
        // асинхронным: у IDisposable и IAsyncDisposable один объект, а порядок вызовов —
        // дело вызывающего.
        using var cts = new CancellationTokenSource();

        var handle = LockRenewer.KeepLocked(
            TimeSpan.FromMilliseconds(20),
            _ => Task.CompletedTask,
            blockImmediately: true,
            cancellationToken: cts.Token);

        await handle.DisposeAsync();
        handle.Dispose();
    }
}
