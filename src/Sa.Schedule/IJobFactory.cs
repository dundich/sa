namespace Sa.Schedule;

public interface IJobFactory
{
    IJobController CreateJobController(IJobSettings settings);
    IJobScheduler CreateJobSchedule(IJobSettings settings);
}