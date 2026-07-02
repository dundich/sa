using Sa.Schedule;

namespace Sa.Outbox.Delivery.Job;

/// <summary>
/// Manages job scheduling
/// </summary>
public interface IDeliveryScheduleProvider
{
    IJobScheduler GetJob(Guid jobId);
}
