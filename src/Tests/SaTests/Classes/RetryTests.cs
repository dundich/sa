using Sa.Classes;

namespace SaTests.Classes;


public class RetryTests
{
    [Fact]
    public async Task Constant_Retry_Succeeds_After_2_Attempts()
    {
        // Arrange
        int attemptCount = 0;
        ValueTask<int> func(int input, CancellationToken token)
        {
            attemptCount++;
            if (attemptCount < 3)
            {
                throw new Exception("Simulated failure");
            }
            return new ValueTask<int>(input);
        }

        // Act
        int result = await Retry.Constant(func, 42, retryCount: 3, waitTime: 10, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(42, result);
        Assert.Equal(3, attemptCount);
    }

    [Fact]
    public async Task Exponential_Retry_Succeeds_After_2_Attempts()
    {
        // Arrange
        int attemptCount = 0;
        ValueTask<int> func(int input, CancellationToken token)
        {
            attemptCount++;
            if (attemptCount < 3)
            {
                throw new Exception("Simulated failure");
            }
            return new ValueTask<int>(input);
        }

        // Act
        int result = await Retry.Exponential(func, 42, retryCount: 3, initialDelay: 10, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(42, result);
        Assert.Equal(3, attemptCount);
    }

    [Fact]
    public async Task Linear_Retry_Succeeds_After_2_Attempts()
    {
        // Arrange
        int attemptCount = 0;
        ValueTask<int> func(int input, CancellationToken token)
        {
            attemptCount++;
            if (attemptCount < 3)
            {
                throw new Exception("Simulated failure");
            }
            return new ValueTask<int>(input);
        }

        // Act
        int result = await Retry.Linear(func, 42, retryCount: 3, initialDelay: 10, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(42, result);
        Assert.Equal(3, attemptCount);
    }

    [Fact]
    public async Task DecorrelatedJitter_Retry_Succeeds_After_2_Attempts()
    {
        // Arrange
        int attemptCount = 0;
        ValueTask<int> func(int input, CancellationToken token)
        {
            attemptCount++;
            if (attemptCount < 3)
            {
                throw new Exception("Simulated failure");
            }
            return new ValueTask<int>(input);
        }

        // Act
        int result = await Retry.Jitter(func, 42, retryCount: 3, initialDelay: 10, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(42, result);
        Assert.Equal(3, attemptCount);
    }

    [Fact]
    public async Task Retry_Throws_Original_Exception_After_Max_Retries()
    {
        // Arrange
        int attemptCount = 0;
        ValueTask<int> func(int input, CancellationToken token)
        {
            attemptCount++;
            throw new Exception("Simulated failure");
        }

        // Act and Assert
        await Assert.ThrowsAsync<Exception>(() => Retry.Constant(func, 42, retryCount: 3, waitTime: 10, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(3, attemptCount);
    }

    [Fact]
    public async Task Retry_Cancels_After_CancellationToken_Is_Cancelled()
    {
        // Arrange
        int attemptCount = 0;
        async ValueTask<int> func(int input, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return 111;
            }

            attemptCount++;
            await Task.Delay(100, CancellationToken.None);
            throw new Exception("Simulated failure");
        }

        using CancellationTokenSource cts = new();

        _ = Task.Run(async () =>
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            await cts.CancelAsync();
        }, TestContext.Current.CancellationToken);

        int result = await Retry.Constant(func, 42, retryCount: 3, waitTime: 10, cancellationToken: cts.Token);

        Assert.True(attemptCount > 0);
        Assert.Equal(111, result);
    }
}
