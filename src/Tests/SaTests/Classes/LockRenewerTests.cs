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
        using (var locker = LockRenewer.KeepLocked(lockExpiration, extendLocked, cancellationToken: cancellationToken))
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
        using (var locker = LockRenewer.KeepLocked(lockExpiration, extendLocked, blockImmediately: true, cancellationToken: cancellationToken))
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
            extensionCount++;
        }

        // Act
        var locker = LockRenewer.KeepLocked(lockExpiration, extendLocked, cancellationToken: cancellationToken);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        locker.Dispose();


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
            Assert.InRange(interval, 80, 150); // Allow some jitter due to scheduling
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

        // Assert
        Assert.True(callTimes.Count > 5, "Should poll frequently with 10ms default");
        for (int i = 1; i < callTimes.Count; i++)
        {
            var intervalMs = (callTimes[i] - callTimes[i - 1]) / 10_000.0; // Ticks to ms
            Assert.InRange(intervalMs, 5, 40); // ~10ms, allow jitter
        }
    }
}
