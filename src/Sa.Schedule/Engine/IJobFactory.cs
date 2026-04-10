namespace Sa.Schedule.Engine;

internal interface IJobFactory
{
    IJobScheduler CreateJobSchedule(IJobSettings settings);
}
