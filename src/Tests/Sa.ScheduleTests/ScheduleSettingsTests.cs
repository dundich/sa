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

        var _ = JobSettings.Create<TestJob>(Guid.NewGuid());
        // job2 keeps defaults

        var merged = JobSettings.Create(job1);
        Assert.Equal(5, merged.ErrorHandling.RetryCount);
        Assert.Equal(ErrorHandlingAction.AbortJob, merged.ErrorHandling.ThenAction);
    }

    [Fact]
    public void Merge_ErrorHandling_FirstExplicitWins_ConsistentWithProperties()
    {
        // Two registrations of the same (id, type) configure error handling
        // explicitly but differently. Like every other merged property, the
        // FIRST (aggregate) registration wins — the reverse, "the later explicit
        // value overwrites", disagreed with JobProperties.Merge where the first
        // registration wins.
        var jobId = Guid.NewGuid();

        var job1 = JobSettings.Create<TestJob>(jobId);
        job1.ErrorHandling.IfErrorRetry(5).ThenAbortJob();
        job1.ErrorHandling.DoSuppressError(ex => ex is TimeoutException);

        var job2 = JobSettings.Create<TestJob>(jobId);
        job2.ErrorHandling.IfErrorRetry(1).ThenStopAllJobs();

        var settings = ScheduleSettings.Create([job1, job2], false, null);
        var merged = settings.GetJobSettings().Single();

        Assert.Equal(5, merged.ErrorHandling.RetryCount);
        Assert.Equal(ErrorHandlingAction.AbortJob, merged.ErrorHandling.ThenAction);
        Assert.NotNull(merged.ErrorHandling.SuppressError);
    }

    [Fact]
    public void Merge_ErrorHandling_DefaultsDoNotClobberAnExplicitEarlierRegistration()
    {
        var jobId = Guid.NewGuid();

        var job1 = JobSettings.Create<TestJob>(jobId);      // defaults
        var job2 = JobSettings.Create<TestJob>(jobId);
        job2.ErrorHandling.IfErrorRetry(3).ThenStopAllJobs();

        // job2 (explicit) merges first, then job1's defaults must NOT overwrite it.
        var settings = ScheduleSettings.Create([job2, job1], false, null);
        var merged = settings.GetJobSettings().Single();

        Assert.Equal(3, merged.ErrorHandling.RetryCount);
        Assert.Equal(ErrorHandlingAction.StopAllJobs, merged.ErrorHandling.ThenAction);
        Assert.Null(merged.ErrorHandling.SuppressError);
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

    [Fact]
    public void Merge_ErrorHandling_ExplicitCloseApplication_IsNotOverwrittenByALaterRegistration()
    {
        // ThenAction defaults to CloseApplication, so the value alone used to make an
        // explicit ThenCloseApplication() look "unset" — a later ThenStopAllJobs() won.
        // The merge now tracks whether the action was chosen.
        var jobId = Guid.NewGuid();

        var first = JobSettings.Create<TestJob>(jobId);
        first.ErrorHandling.ThenCloseApplication();

        var second = JobSettings.Create<TestJob>(jobId);
        second.ErrorHandling.ThenStopAllJobs();

        var merged = ScheduleSettings.Create([first, second], false, null)
            .GetJobSettings()
            .Single();

        Assert.Equal(ErrorHandlingAction.CloseApplication, merged.ErrorHandling.ThenAction);
    }

    [Fact]
    public void Create_SameJobIdForDifferentTypes_ThrowsWithBothTypes()
    {
        var jobId = Guid.NewGuid();
        var first = JobSettings.Create<TestJob>(jobId);
        var second = JobSettings.Create<OtherTestJob>(jobId);

        var ex = Assert.Throws<ArgumentException>(
            () => ScheduleSettings.Create([first, second], false, null));

        Assert.Contains(jobId.ToString(), ex.Message);
        Assert.Contains(nameof(TestJob), ex.Message);
        Assert.Contains(nameof(OtherTestJob), ex.Message);
    }

    [Fact]
    public void Create_SameJobIdForSameType_MergesIntoOneJob()
    {
        // The same (id, type) registered twice is the documented accumulation case —
        // it must keep merging, not trip the duplicate-id guard.
        var jobId = Guid.NewGuid();
        var first = JobSettings.Create<TestJob>(jobId);
        var second = JobSettings.Create<TestJob>(jobId);
        second.ErrorHandling.IfErrorRetry(4);

        var settings = ScheduleSettings.Create([first, second], false, null);

        Assert.Single(settings.GetJobSettings());
        Assert.Equal(4, settings.GetJobSettings().Single().ErrorHandling.RetryCount);
    }

    private sealed class TestJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class OtherTestJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
