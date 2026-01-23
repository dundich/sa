namespace Sa.Schedule.Engine;

internal interface IJobFactory
{
    IJobController CreateJobController(IJobSettings settings);
    IJobScheduler CreateJobSchedule(IJobSettings settings);
}
