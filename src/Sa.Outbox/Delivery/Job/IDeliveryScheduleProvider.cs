using Sa.Schedule;

namespace Sa.Outbox.Delivery.Job;

public interface IDeliveryScheduleProvider
{
    IJobScheduler GetJob(Guid jobId);

    int GetInstanceCount(Guid jobId) => GetJob(jobId).ConcurrencyLimit;
    void SetInstanceCount(Guid jobId, int count) => GetJob(jobId).ConcurrencyLimit = count;
}
