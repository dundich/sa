using Sa.Classes;

namespace SaTests.Classes;

public class AsyncManualResetEventTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public void Constructor_WithInitialSetTrue_ShouldBeSet()
    {
        // Arrange & Act
        var resetEvent = new AsyncManualResetEvent(true);

        // Assert
        Assert.True(resetEvent.IsSet);
    }

    [Fact]
    public void Constructor_WithInitialSetFalse_ShouldNotBeSet()
    {
        // Arrange & Act
        var resetEvent = new AsyncManualResetEvent(false);

        // Assert
        Assert.False(resetEvent.IsSet);
    }

    [Fact]
    public void Constructor_Default_ShouldNotBeSet()
    {
        // Arrange & Act
        var resetEvent = new AsyncManualResetEvent();

        // Assert
        Assert.False(resetEvent.IsSet);
    }

    [Fact]
    public async Task WaitAsync_WhenSet_ReturnsCompletedTask()
    {
        // Arrange
        var resetEvent = new AsyncManualResetEvent(true);

        // Act
        var task = resetEvent.WaitAsync(TestToken);

        // Assert
        Assert.True(task.IsCompleted);
        await task; // Should not throw
    }

    [Fact]
    public async Task WaitAsync_WhenNotSet_ReturnsIncompleteTask()
    {
        // Arrange
        var resetEvent = new AsyncManualResetEvent(false);

        // Act
        var task = resetEvent.WaitAsync(TestToken);

        // Assert
        Assert.False(task.IsCompleted);

        // Cleanup
        resetEvent.Set();
        await task;
    }

    [Fact]
    public async Task WaitAsync_AfterSet_ReturnsCompletedTask()
    {
        // Arrange
        var resetEvent = new AsyncManualResetEvent(false);

        // Act
        resetEvent.Set();
        var task = resetEvent.WaitAsync(TestToken);

        // Assert
        Assert.True(task.IsCompleted);
        await task;
    }

    [Fact]
    public async Task WaitAsync_MultipleWaiters_AllCompleteWhenSet()
    {
        // Arrange
        var resetEvent = new AsyncManualResetEvent(false);
        const int waiterCount = 10;
        var tasks = new Task[waiterCount];

        // Act
        for (int i = 0; i < waiterCount; i++)
        {
            tasks[i] = resetEvent.WaitAsync(TestToken);
        }

        // Assert - all tasks should be incomplete
        foreach (var task in tasks)
        {
            Assert.False(task.IsCompleted);
        }

        // Act - set the event
        resetEvent.Set();

        // Assert - all tasks should complete
        await Task.WhenAll(tasks);
        foreach (var task in tasks)
        {
            Assert.True(task.IsCompleted);
        }
    }

    [Fact]
    public async Task Set_WhenAlreadySet_DoesNothing()
    {
        // Arrange
        var resetEvent = new AsyncManualResetEvent(true);

        // Act - should not throw
        resetEvent.Set();
        resetEvent.Set();

        // Assert
        Assert.True(resetEvent.IsSet);
        await resetEvent.WaitAsync(TestToken); // Should complete immediately
    }

    [Fact]
    public void Reset_WhenSet_ClearsTheSignal()
    {
        // Arrange
        var resetEvent = new AsyncManualResetEvent(true);

        // Act
        resetEvent.Reset();

        // Assert
        Assert.False(resetEvent.IsSet);
    }

    [Fact]
    public void Reset_WhenNotSet_RemainsNotSet()
    {
        // Arrange
        var resetEvent = new AsyncManualResetEvent(false);

        // Act
        resetEvent.Reset();

        // Assert
        Assert.False(resetEvent.IsSet);
    }

    [Fact]
    public async Task WaitAsync_WithCancellationToken_CanBeCancelled()
    {
        // Arrange
        var resetEvent = new AsyncManualResetEvent(false);
        using var cts = new CancellationTokenSource();

        // Act
        var task = resetEvent.WaitAsync(cts.Token);

        // Assert - task should be waiting
        Assert.False(task.IsCompleted);

        // Act - cancel
        await cts.CancelAsync();

        // Assert - task should be cancelled
        await Assert.ThrowsAsync<TaskCanceledException>(() => task);
    }

    [Fact]
    public async Task WaitAsync_AfterReset_ShouldWaitAgain()
    {
        // Arrange
        var resetEvent = new AsyncManualResetEvent(true);

        // Act - first wait should complete immediately
        await resetEvent.WaitAsync(TestToken);

        // Reset and wait again
        resetEvent.Reset();
        var secondWaitTask = resetEvent.WaitAsync(TestToken);

        // Assert - second wait should not complete
        Assert.False(secondWaitTask.IsCompleted);

        // Cleanup
        resetEvent.Set();
        await secondWaitTask;
    }

    [Fact]
    public async Task StressTest_MultipleSetResetCycles()
    {
        // Arrange
        var resetEvent = new AsyncManualResetEvent(false);
        const int cycles = 100;

        for (int i = 0; i < cycles; i++)
        {
            // Act - set and verify
            resetEvent.Set();
            await resetEvent.WaitAsync(TestToken); // Should complete immediately
            Assert.True(resetEvent.IsSet);

            // Act - reset and verify
            resetEvent.Reset();
            Assert.False(resetEvent.IsSet);
        }
    }

    [Fact]
    public async Task ConcurrentAccess_MultipleThreads()
    {
        // Arrange
        var resetEvent = new AsyncManualResetEvent(false);
        const int threadCount = 10;
        var tasks = new Task[threadCount * 2]; // half waiters, half setters
        var completedWaiters = 0;

        // Act - start multiple threads that wait and set
        for (int i = 0; i < threadCount; i++)
        {
            // Waiter task
            tasks[i] = Task.Run(async () =>
            {
                await resetEvent.WaitAsync(TestToken);
                Interlocked.Increment(ref completedWaiters);
            }, TestToken);

            // Setter task (start slightly later)
            tasks[i + threadCount] = Task.Run(async () =>
            {
                await Task.Delay(10);
                resetEvent.Set();
            }, TestToken);
        }

        // Wait for all tasks
        await Task.WhenAll(tasks);

        // Assert - all waiters should have completed
        Assert.Equal(threadCount, completedWaiters);
        Assert.True(resetEvent.IsSet);
    }

    [Fact]
    public async Task WaitAsync_WithAlreadyCancelledToken_ThrowsImmediately()
    {
        // Arrange
        var resetEvent = new AsyncManualResetEvent(false);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAsync<TaskCanceledException>(() => resetEvent.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task WaitAsync_AfterResetButBeforeSet_ShouldWait()
    {
        // Arrange
        var resetEvent = new AsyncManualResetEvent(true);

        // Reset and start waiting
        resetEvent.Reset();
        var waitTask = resetEvent.WaitAsync(TestToken);

        // Assert - should be waiting
        Assert.False(waitTask.IsCompleted);

        // Act - set and verify completion
        resetEvent.Set();
        await waitTask;
        Assert.True(waitTask.IsCompleted);
    }
}
