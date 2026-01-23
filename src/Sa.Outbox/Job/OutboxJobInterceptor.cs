using Sa.Schedule;

namespace Sa.Outbox.Job;

internal sealed class OutboxJobInterceptor(IEnumerable<IOutboxJobInterceptor> interceptors) : IJobInterceptor
{
    public async Task OnHandle(IJobContext context, Func<Task> next, object? key, CancellationToken cancellationToken)
    {
        bool hasInterceptors = false;
        foreach (IOutboxJobInterceptor item in interceptors)
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
