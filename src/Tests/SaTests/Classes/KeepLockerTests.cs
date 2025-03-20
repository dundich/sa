using Sa.Classes;

namespace SaTests.Classes;

public class KeepLockerTests
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
        using (var locker = KeepLocker.KeepLocked(lockExpiration, extendLocked, cancellationToken: cancellationToken))
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
        using (var locker = KeepLocker.KeepLocked(lockExpiration, extendLocked, blockImmediately: true, cancellationToken: cancellationToken))
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
        var locker = KeepLocker.KeepLocked(lockExpiration, extendLocked, cancellationToken: cancellationToken);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        locker.Dispose();


        var expected = extensionCount;
        // Assert
        Assert.True(extensionCount > 0, "The lock should have been extended immediately.");

        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(expected, extensionCount);
    }
}