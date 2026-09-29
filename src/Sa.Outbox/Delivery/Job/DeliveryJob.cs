using Sa.Schedule;

namespace Sa.Outbox.Delivery.Job;


public interface IDeliveryJob : IJob;


internal sealed class DeliveryJob<TMessage>(
    IDeliveryProcessor processor,
    IOutboxConsumerManager settingsManager) : IDeliveryJob
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
    /// </summary>
    public async Task Execute(IJobContext context, CancellationToken cancellationToken)
    {
        var settings = settingsManager.Get(context.JobName)
            ?? throw new InvalidOperationException($"No OutboxConsumerSettings for consumer group '{context.JobName}'.");

        await processor.ProcessMessages<TMessage>(settings, cancellationToken).ConfigureAwait(false);
    }
}
