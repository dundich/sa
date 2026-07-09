using Sa.Schedule;

namespace Sa.Outbox.Delivery.Job;

internal sealed class DeliveryJobInterceptor(IEnumerable<IOutboxDeliveryJobInterceptor> interceptors)
    : IJobInterceptor
{
    public async Task OnHandle(IJobContext context, Func<Task> next, object? key, CancellationToken cancellationToken)
    {
        bool hasInterceptors = false;
        foreach (IOutboxDeliveryJobInterceptor item in interceptors)
        {
            hasInterceptors = true;
            await item.OnHandle(context, next, key, cancellationToken);
        }

        if (!hasInterceptors)
        {
            await next();
        }
    }
}
