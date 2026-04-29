using Sa.Schedule;

namespace Sa.Outbox.Delivery.Job;

internal sealed class DeliveryScheduleProvider(IScheduler scheduler) : IDeliveryScheduleProvider
{
    public IJobScheduler GetJob(Guid jobId)
        => scheduler.GetSchedule(jobId) ?? throw new InvalidOperationException();
}
