using Sa.Schedule;
using Sa.Schedule.Engine;
using Sa.Schedule.Settings;

namespace Sa.ScheduleTests;

public class JobExceptionTests
{
    [Fact]
    public void ContextSnapshot_JobName_MatchesContext()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties.WithName("TestJobName");
        var context = CreateContext(settings);

        var innerEx = new InvalidOperationException("inner error");
        var jobException = new JobException(context, innerEx);

        Assert.Equal("TestJobName", jobException.ContextSnapshot.JobName);
    }

    [Fact]
    public void ContextSnapshot_NumIterations_CapturedCorrectly()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var context = CreateContext(settings);
        context.NumIterations = 5;

        var innerEx = new Exception("error");
        var jobException = new JobException(context, innerEx);

        Assert.Equal(5UL, jobException.ContextSnapshot.NumIterations);
    }

    [Fact]
    public void ContextSnapshot_FailedIterations_CapturedCorrectly()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var context = CreateContext(settings);
        context.FailedIterations = 3;

        var innerEx = new Exception("error");
        var jobException = new JobException(context, innerEx);

        Assert.Equal(3UL, jobException.ContextSnapshot.FailedIterations);
    }

    [Fact]
    public void ContextSnapshot_CompletedIterations_CapturedCorrectly()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var context = CreateContext(settings);
        context.CompetedIterations = 10;

        var innerEx = new Exception("error");
        var jobException = new JobException(context, innerEx);

        Assert.Equal(10UL, jobException.ContextSnapshot.CompetedIterations);
    }

    [Fact]
    public void ContextSnapshot_ExecuteAt_CapturedCorrectly()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var context = CreateContext(settings);
        var now = DateTimeOffset.UtcNow;
        context.ExecuteAt = now;

        var innerEx = new Exception("error");
        var jobException = new JobException(context, innerEx);

        Assert.Equal(now, jobException.ContextSnapshot.ExecuteAt);
    }

    [Fact]
    public void ContextSnapshot_FailedRetries_CapturedCorrectly()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var context = CreateContext(settings);
        context.FailedRetries = 2;

        var innerEx = new Exception("error");
        var jobException = new JobException(context, innerEx);

        Assert.Equal(2, jobException.ContextSnapshot.FailedRetries);
    }

    [Fact]
    public void ContextSnapshot_LastErrorMessage_CapturedFromLastError()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var context = CreateContext(settings);
        // LastError is a JobException, its .Message format is "[JobName] job error"
        context.LastError = new JobException(context, new Exception("inner"));

        var outerEx = new Exception("new error");
        var jobException = new JobException(context, outerEx);

        // LastErrorMessage reads LastError.Message which is the JobException's formatted message
        Assert.NotNull(jobException.ContextSnapshot.LastErrorMessage);
        Assert.Contains("job error", jobException.ContextSnapshot.LastErrorMessage!);
    }

    [Fact]
    public void ContextSnapshot_StackDepth_CountsEntries()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var context = CreateContext(settings);
        var stack = context;
        stack.Stack.Enqueue(stack.Clone());
        stack.Stack.Enqueue(stack.Clone());
        stack.Stack.Enqueue(stack.Clone());

        var innerEx = new Exception("error");
        var jobException = new JobException(context, innerEx);

        Assert.Equal(3, jobException.ContextSnapshot.StackDepth);
    }

    [Fact]
    public void InnerException_ProvidedInnerException()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var context = CreateContext(settings);
        var innerEx = new ArgumentException("arg error");

        var jobException = new JobException(context, innerEx);

        Assert.Same(innerEx, jobException.InnerException);
    }

    [Fact]
    public void Message_IncludesJobNameAndError()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties.WithName("MyJob");
        var context = CreateContext(settings);

        var innerEx = new Exception("boom");
        var jobException = new JobException(context, innerEx);

        Assert.Contains("MyJob", jobException.Message);
        Assert.Contains("job error", jobException.Message);
    }

    [Fact]
    public void ContextSnapshot_CreatedAt_CapturedCorrectly()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var context = CreateContext(settings);
        var expectedCreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        context.CreatedAt = expectedCreatedAt;

        var innerEx = new Exception("error");
        var jobException = new JobException(context, innerEx);

        Assert.Equal(expectedCreatedAt, jobException.ContextSnapshot.CreatedAt);
    }

    private static JobContext CreateContext(IJobSettings settings) => new(settings);

    sealed class TestJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
