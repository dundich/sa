namespace Sa.Partitional.PostgreSqlTests;

/// <summary>
/// Waiting helpers for tests that observe a background job.
/// </summary>
internal static class AwaitHelper
{
    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/> elapses.
    /// </summary>
    /// <remarks>
    /// Used instead of a fixed <c>Task.Delay</c> after starting a scheduler: the job runs on its own
    /// thread, and a hard-coded sleep either flakes on a loaded machine or wastes that time on every
    /// run. The last failure of <paramref name="condition"/> is rethrown when the timeout wins, so a
    /// broken job is reported by its own error rather than as a bare timeout.
    /// </remarks>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="timeout">How long to keep polling.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <exception cref="TimeoutException">The condition never held within the timeout.</exception>
    public static async Task UntilTrueAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);

        TimeSpan poll = TimeSpan.FromMilliseconds(50);
        TimeSpan waited = TimeSpan.Zero;
        Exception? lastFailure = null;

        while (waited < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await condition().ConfigureAwait(false)) return;

                lastFailure = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = ex;
            }

            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            waited += poll;
        }

        if (lastFailure is not null)
        {
            throw new TimeoutException(
                $"The condition did not hold within {timeout} (last failure: {lastFailure.Message}).",
                lastFailure);
        }

        throw new TimeoutException($"The condition did not hold within {timeout}.");
    }
}
