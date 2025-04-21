namespace Sa.Classes;

public static class KeepLocker
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
                    await extendLocked(cancellationToken);
                }

                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    await extendLocked(cancellationToken);
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
}