using Sa.Schedule;

namespace Sa.Outbox.Delivery.Job;


public interface IDeliveryJob : IJob;


internal sealed class DeliveryJob<TMessage>(
    IDeliveryProcessor processor,
    IOutboxConsumerManager settingsManager,
    IOutboxIdleStateRegistry idleStates) : IDeliveryJob
{
    /// <summary>
    /// Scheduled entry point: resolves the consumer group settings and runs one delivery cycle.
    /// <para>
    /// The settings snapshot is taken once, here, and stays fixed for the whole cycle. Runtime
    /// changes through <see cref="IOutboxConsumerManager.Pause(string)"/> /
    /// <see cref="IOutboxConsumerManager.Resume(string)"/> are therefore picked up by the
    /// <em>next</em> job execution, not the current one. That is intentional — see
    /// <see cref="Sa.Outbox.Delivery.DeliveryProcessor.ProcessMessages{TMessage}"/>.
    /// </para>
    /// <para>
    /// After the cycle the run outcome is reported into the group's <see cref="OutboxIdleState"/>,
    /// which is what the adaptive <see cref="Sa.Outbox.Delivery.AdaptiveIdleTiming"/> reads on the
    /// next iteration: work resets the poll interval, an empty queue backs it off.
    /// </para>
    /// </summary>
    public async Task Execute(IJobContext context, CancellationToken cancellationToken)
    {
        var settings = settingsManager.Get(context.JobName)
            ?? throw new InvalidOperationException($"No OutboxConsumerSettings for consumer group '{context.JobName}'.");

        long processed = await processor.ProcessMessages<TMessage>(settings, cancellationToken).ConfigureAwait(false);

        OutboxIdleState? idleState = idleStates.Get(context.JobName);
        if (idleState is null) return;

        // While paused a run always yields zero. Reset instead of backing off, so that resuming
        // starts from the base interval rather than the interval accumulated while paused.
        if (settings.Paused)
            idleState.Reset();
        else
            idleState.Report(processed);
    }
}
