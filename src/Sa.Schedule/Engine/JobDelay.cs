namespace Sa.Schedule.Engine;

/// <summary>
/// Cancellation-aware delay that is safe for arbitrarily long waits.
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> rejects a single delay greater than
/// <see cref="int.MaxValue"/> milliseconds (~24.9 days) with an
/// <see cref="ArgumentOutOfRangeException"/>, so anything longer — a cron occurrence months
/// out, <c>EveryDays(50)</c>, a long <c>InitialDelay</c> — is split into timer-sized chunks.
/// The exception matters more than the wait: it would escape
/// <see cref="IJobController.CanExecute"/>/WaitToRun, which run outside the runner's
/// per-iteration try/catch, fault the queue item and take the slot down through the queue's
/// faulted-item strategy — the job would die silently while <c>IsStarted</c> still reported
/// <c>true</c>, bypassing the job's error policy entirely.
/// </summary>
internal static class JobDelay
{
    /// <summary>The documented upper bound of a single <c>Task.Delay</c> — <c>int.MaxValue</c> ms.</summary>
    private static readonly TimeSpan MaxSingleDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Waits for <paramref name="delay"/>. Non-positive delays are a no-op;
    /// a cancelled token throws <see cref="OperationCanceledException"/> as usual.
    /// </summary>
    public static Task Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero) return Task.CompletedTask;

        if (delay <= MaxSingleDelay) return Task.Delay(delay, cancellationToken);

        return WaitInChunks(delay, cancellationToken);
    }

    private static async Task WaitInChunks(TimeSpan delay, CancellationToken cancellationToken)
    {
        while (delay > MaxSingleDelay)
        {
            // Cancellation is re-checked between chunks, so a long wait still
            // reacts to shutdown within one timer tick of the token firing.
            await Task.Delay(MaxSingleDelay, cancellationToken).ConfigureAwait(false);

            delay -= MaxSingleDelay;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
