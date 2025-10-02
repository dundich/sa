using System.Diagnostics;

namespace Sa.Classes;

internal static class LockRenewer
{
    public static IDisposable KeepLocked(TimeSpan lockExpiration, Func<CancellationToken, Task> extendLocked, bool blockImmediately = false, CancellationToken cancellationToken = default)
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
            catch
            {
                // ignore
            }
        }, cancellationToken);

        IDisposable keeper = new DisposableTimer(timer, task);

        return keeper;
    }

    private sealed class DisposableTimer(PeriodicTimer timer, Task task) : IDisposable
    {
        public void Dispose()
        {
            timer.Dispose();
            task.Wait(); // Ожидание завершения задачи перед освобождением ресурсов
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

        try
        {
            while (sw.Elapsed < timeout)
            {
                if (!cancellationToken.IsCancellationRequested && await predicate(cancellationToken).ConfigureAwait(false))
                    return true;

                await Task.Delay(interval, cancellationToken);
            }
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
        {
            /* ignore */
        }

        return false;
    }
}