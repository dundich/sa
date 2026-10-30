using Sa.Schedule;

namespace Sa.Outbox.Delivery;

/// <summary>
/// An <see cref="IJobTiming"/> whose next occurrence is driven by a consumer group's
/// <see cref="OutboxIdleState"/>: the base interval while work is flowing, and progressively
/// longer waits while the queue stays empty.
/// <para>
/// <see cref="Sa.Schedule.Engine.JobController"/> consults the timing before every iteration and
/// waits <c>GetNextOccurrence(now) - now</c>, so reporting the run outcome into the state before
/// that point is what makes the schedule adaptive.
/// </para>
/// </summary>
internal sealed class AdaptiveIdleTiming(OutboxIdleState state) : IJobTiming
{
    /// <inheritdoc/>
    public string TimingName => "outbox-idle";

    /// <inheritdoc/>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset dateTime)
        => dateTime + state.CurrentInterval;
}
