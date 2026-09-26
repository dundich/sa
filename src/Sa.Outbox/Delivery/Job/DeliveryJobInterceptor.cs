using Sa.Schedule;

namespace Sa.Outbox.Delivery.Job;

internal sealed class DeliveryJobInterceptor(IEnumerable<IOutboxDeliveryJobInterceptor> interceptors)
    : IJobInterceptor
{
    /// <summary>
    /// Fans the Sa.Schedule interceptor chain out to the registered outbox interceptors.
    /// <para>
    /// The outbox interceptors are composed as a <em>chain</em>, exactly like
    /// <see cref="Sa.Schedule.Engine.JobExecutor"/> composes the schedule's own interceptors: each
    /// one receives a <c>next</c> that runs the remainder of the pipeline.
    /// </para>
    /// <para>
    /// Handing the same <c>next</c> to every interceptor would invoke it once per interceptor, so the
    /// delivery job body would run N times per tick for N registered interceptors — and since an
    /// interceptor may skip <c>next</c> entirely, the possible execution paths multiply. Do not
    /// "simplify" this into a loop over a shared <c>next</c>.
    /// </para>
    /// </summary>
    public Task OnHandle(IJobContext context, Func<Task> next, object? key, CancellationToken cancellationToken)
    {
        Func<Task> current = next;

        foreach (IOutboxDeliveryJobInterceptor interceptor in interceptors)
        {
            var nextInChain = current;

            current = () => interceptor.OnHandle(context, nextInChain, key, cancellationToken);
        }

        return current();
    }
}
