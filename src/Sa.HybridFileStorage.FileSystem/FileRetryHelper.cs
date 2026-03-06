namespace Sa.HybridFileStorage.FileSystem;

internal static class FileRetryHelper
{
    public static async Task RetryAsync(
        Action action,
        int maxRetries = 3,
        int baseDelayMs = 100,
        CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                action();
                return;
            }
            catch (IOException ex) when (attempt < maxRetries - 1)
            {
                lastException = ex;
                await Task.Delay(baseDelayMs * (int)Math.Pow(2, attempt), cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException("Retry failed");
    }
}
