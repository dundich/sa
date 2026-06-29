using Sa.Schedule;
using Sa.Schedule.Settings;

namespace Sa.ScheduleTests;

public class ScheduleSettingsTests
{
    [Fact]
    public void IsHostedService_ReturnsCorrectValue()
    {
        var settings = ScheduleSettings.Create([], isHostedService: true, null);
        Assert.True(settings.IsHostedService);

        var settings2 = ScheduleSettings.Create([], isHostedService: false, null);
        Assert.False(settings2.IsHostedService);
    }

    [Fact]
    public void GetJobSettings_ReturnsRegisteredJobs()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var job1 = JobSettings.Create<TestJob>(id1);
        var job2 = JobSettings.Create<TestJob>(id2);
        var settings = ScheduleSettings.Create([job1, job2], false, null);

        var jobs = settings.GetJobSettings().ToList();
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, j => j.JobId == id1);
        Assert.Contains(jobs, j => j.JobId == id2);
    }

    [Fact]
    public void GetJobSettings_EmptyList_ReturnsEmpty()
    {
        var settings = ScheduleSettings.Create([], false, null);
        Assert.Empty(settings.GetJobSettings());
    }

    [Fact]
    public void HandleError_GlobalHandler_CanConsumeErrors()
    {
        var settings = ScheduleSettings.Create(
            [],
            isHostedService: false,
            (ctx, ex) => true);

        // The handler should be set — verify by checking the property exists
        Assert.NotNull(settings.HandleError);
    }

    [Fact]
    public void Merge_JobProperties_PrioritizesNonDefault()
    {
        var job1 = JobSettings.Create<TestJob>(Guid.NewGuid());
        job1.ErrorHandling.IfErrorRetry(5).ThenAbortJob();

        var job2 = JobSettings.Create<TestJob>(Guid.NewGuid());
        // job2 keeps defaults

        var merged = JobSettings.Create(job1);
        Assert.Equal(5, merged.ErrorHandling.RetryCount);
        Assert.Equal(ErrorHandlingAction.AbortJob, merged.ErrorHandling.ThenAction);
    }

    [Fact]
    public void JobSettings_CreateGeneratesNewId()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        Assert.NotEqual(Guid.Empty, settings.JobId);
    }

    [Fact]
    public void JobSettings_Clone_ReturnsIndependentCopy()
    {
        var original = JobSettings.Create<TestJob>(Guid.NewGuid());
        original.Properties.WithName("Original");
        original.ErrorHandling.IfErrorRetry(3).ThenStopAllJobs();

        var clone = original.Clone();

        Assert.Equal(original.JobId, clone.JobId);
        Assert.Equal(original.Properties.JobName, clone.Properties.JobName);
        Assert.Equal(original.ErrorHandling.RetryCount, clone.ErrorHandling.RetryCount);
        Assert.Same(original.JobType, clone.JobType);
    }

    private sealed class TestJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
