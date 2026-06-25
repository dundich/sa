using System.Diagnostics;

namespace Sa.Classes;

internal static class LockRenewer
{
    public static IAsyncDisposable KeepLocked(
        TimeSpan lockExpiration,
        Func<CancellationToken, Task> extendLocked,
        bool blockImmediately = false,
        CancellationToken cancellationToken = default)
    {
        var timer = new PeriodicTimer(lockExpiration);
        var task = Task.Run(async () =>
        {
            try
            {
                if (blockImmediately)
                {
                    await extendLocked(cancellationToken).ConfigureAwait(false);
                }

                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    await extendLocked(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
        }, cancellationToken);

        return new DisposableTimer(timer, task);
    }

    private sealed class DisposableTimer(PeriodicTimer Timer, Task Task) : IDisposable, IAsyncDisposable
    {
        public void Dispose()
        {
            Timer.Dispose();
            // Fire-and-forget wait — avoids blocking the caller on task completion
            _ = Task.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public async ValueTask DisposeAsync()
        {
            Timer.Dispose();
            await Task;
        }
    }


    public static async Task<bool> WaitForConditionAsync(
        Func<CancellationToken, Task<bool>> predicate,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(10);
        var sw = Stopwatch.StartNew();
        var timer = new PeriodicTimer(interval);

        try
        {
            while (sw.Elapsed < timeout)
            {
                if (!cancellationToken.IsCancellationRequested
                    && await predicate(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            /* ignore */
        }

        return false;
    }
}
