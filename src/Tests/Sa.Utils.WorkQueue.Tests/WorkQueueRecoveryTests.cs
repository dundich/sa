namespace Sa.Utils.WorkQueue.Tests;

public class SaWorkQueueRecoveryTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private class TestProcessor(int failOnValue = -1) : ISaWork<int>
    {
        public int ProcessedCount;

        public Task Execute(int input, CancellationToken _)
        {
            if (input == failOnValue) throw new InvalidOperationException("Simulated processing failure");
            Interlocked.Increment(ref ProcessedCount);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ForceCancelReaders_ThenResetConcurrency_ReadersRecoverAndProcessItems()
    {
        // Arrange
        var options = new SaWorkQueueOptions<int>(new TestProcessor())
        {
            ConcurrencyLimit = 2,
            MaxConcurrency = 4
        };

        using var queue = new SaWorkQueue<int>(options);

        // Добавим несколько задач
        await queue.Enqueue(1, TestToken);
        await queue.Enqueue(2, TestToken);
        await queue.WaitForIdleAsync(TestToken);

        Assert.Equal(2, ((TestProcessor)options.Processor).ProcessedCount);

        await queue.ForceCancelReadersAsync();
        await Task.Delay(100, TestToken);

        Assert.True(queue.IsIdle());


        queue.ConcurrencyLimit = 2;

        await Task.Delay(100, TestToken);

        await queue.Enqueue(3, TestToken);
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(3, ((TestProcessor)options.Processor).ProcessedCount);
    }

    [Fact]
    public async Task FaultedItem_DoesNotBreakReader_QueueContinuesProcessing()
    {
        // Arrange
        var statuses = new List<SaWorkStatus>();
        var processor = new TestProcessor(failOnValue: -1);
        var options = new SaWorkQueueOptions<int>(processor)
        {
            ConcurrencyLimit = 1,
            StatusChanged = (item, status, ex) => statuses.Add(status),
            HandleItemFaulted = (_, __) => SaExecutionErrorStrategy.Continue
        };
        using var queue = new SaWorkQueue<int>(options);

        // Act
        await queue.Enqueue(1, TestToken);          // OK

        await queue.Enqueue(-1, TestToken);         // Fail

        await queue.Enqueue(2, TestToken);          // OK после fail

        await queue.WaitForIdleAsync(TestToken);

        // Assert
        Assert.Equal(2, processor.ProcessedCount);
        Assert.Equal(new[] { SaWorkStatus.Running, SaWorkStatus.Completed,    // item 1
                          SaWorkStatus.Running, SaWorkStatus.Faulted,      // item -1
                          SaWorkStatus.Running, SaWorkStatus.Completed },  // item 2
                     statuses);

        Assert.True(queue.IsIdle());
    }


    [Fact]
    public async Task FaultedItem_BreakReader_QueueContinuesProcessing()
    {
        // Arrange
        var statuses = new List<SaWorkStatus>();
        var processor = new TestProcessor(failOnValue: -1);
        var options = new SaWorkQueueOptions<int>(processor)
        {
            ConcurrencyLimit = 2,
            StatusChanged = (item, status, ex) => statuses.Add(status),
            HandleItemFaulted = (_, __) => SaExecutionErrorStrategy.StopReader
        };
        using var queue = new SaWorkQueue<int>(options);

        // Act
        await queue.Enqueue(1, TestToken);          // OK

        await queue.Enqueue(-1, TestToken);         // Fail

        await queue.WaitForIdleAsync(TestToken);

        await queue.Enqueue(1, TestToken);          // OK

        await queue.WaitForIdleAsync(TestToken);

        // Assert
        Assert.Equal(2, processor.ProcessedCount);

        Assert.Equal(1, queue.ConcurrencyLimit);
    }

    [Fact]
    public async Task FaultedItem_ShutdownQueue_QueueFailedProcessing()
    {
        // Arrange
        var statuses = new List<SaWorkStatus>();
        var processor = new TestProcessor(failOnValue: -1);
        var options = new SaWorkQueueOptions<int>(processor)
        {
            ConcurrencyLimit = 2,
            StatusChanged = (item, status, ex) => statuses.Add(status),
            HandleItemFaulted = (_, __) => SaExecutionErrorStrategy.ShutdownQueue
        };

        using var queue = new SaWorkQueue<int>(options);

        // Act
        await queue.Enqueue(1, TestToken);          // OK

        await queue.Enqueue(-1, TestToken);         // Fail

        await queue.WaitForIdleAsync(TestToken);


        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await queue.Enqueue(2, cancellationToken: CancellationToken.None);
        });

        Assert.False(queue.IsEnabled);
        Assert.NotNull(queue.ShutdownError);
    }
}
