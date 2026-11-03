using Sa.Schedule;

namespace Sa.Outbox;

/// <summary>
/// Marker interface that extends <see cref="IJobInterceptor"/> to enable outbox-specific interception hooks
/// around delivery job lifecycle events (before start, after completion, on failure).
/// </summary>
public interface IOutboxDeliveryJobInterceptor : IJobInterceptor
{
}
