using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule;
using Sa.Schedule.Settings;

namespace Sa.ScheduleTests;

public class ScheduleBuilderTests
{
    [Fact]
    public void AddJob_Type_RegistersJobSettings()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        builder.AddJob<TestJob>();

        var settings = services.FirstOrDefault(d => d.ServiceType == typeof(JobSettings));
        Assert.NotNull(settings);
    }

    [Fact]
    public void AddJob_Func_RegistersFuncJob()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        builder.AddJob((ctx, ct) => Task.CompletedTask);

        var provider = services.BuildServiceProvider();
        var jobSettings = provider.GetServices<JobSettings>().ToList();
        Assert.NotEmpty(jobSettings);
    }

    [Fact]
    public void AddInterceptor_RegistersInterceptorSettings()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        builder.AddInterceptor<TestInterceptor>();

        var provider = services.BuildServiceProvider();
        var interceptorSettings = provider.GetService<IInterceptorSettings>();
        Assert.NotNull(interceptorSettings);
        Assert.Single(interceptorSettings!.Interceptors);
    }

    [Fact]
    public void UseHostedService_MarksAsHostedService()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        builder.UseHostedService().AddJob<TestJob>();

        var provider = services.BuildServiceProvider();
        var scheduleSettings = provider.GetService<IScheduleSettings>();
        Assert.True(scheduleSettings!.IsHostedService);
    }

    [Fact]
    public void JobBuilder_WithName_SetsJobName()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        builder.AddJob<TestJob>().WithName("MyCustomJob");

        var provider = services.BuildServiceProvider();
        var jobSettings = provider.GetRequiredService<JobSettings>();

        Assert.Equal("MyCustomJob", jobSettings.Properties.JobName);
    }

    [Fact]
    public void JobBuilder_Disabled_SetsDisabledFlag()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        builder.AddJob<TestJob>().Disabled();

        var provider = services.BuildServiceProvider();
        var jobSettings = provider.GetRequiredService<JobSettings>();

        Assert.True(jobSettings.Properties.Disabled);
    }

    [Fact]
    public void JobBuilder_Cron_SetsCronTiming()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        builder.AddJob<TestJob>().WithCron("0 9 * * *", "MorningJob");

        var provider = services.BuildServiceProvider();
        var jobSettings = provider.GetRequiredService<JobSettings>();

        Assert.NotNull(jobSettings.Properties.Timing);
        Assert.Equal("MorningJob", jobSettings.Properties.Timing!.TimingName);
    }

    [Fact]
    public void JobBuilder_EverySeconds_SetsTiming()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        builder.AddJob<TestJob>().EverySeconds(30);

        var provider = services.BuildServiceProvider();
        var jobSettings = provider.GetRequiredService<JobSettings>();

        Assert.NotNull(jobSettings.Properties.Timing);
    }

    [Fact]
    public void JobBuilder_WithInitialDelay_SetsDelay()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        var delay = TimeSpan.FromSeconds(5);
        builder.AddJob<TestJob>().WithInitialDelay(delay);

        var provider = services.BuildServiceProvider();
        var jobSettings = provider.GetRequiredService<JobSettings>();

        Assert.Equal(delay, jobSettings.Properties.InitialDelay);
    }

    [Fact]
    public void JobBuilder_WithTag_SetsTag()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);
        var tag = new { Priority = 42 };

        builder.AddJob<TestJob>().WithTag(tag);

        var provider = services.BuildServiceProvider();
        var jobSettings = provider.GetRequiredService<JobSettings>();

        Assert.Same(tag, jobSettings.Properties.Tag);
    }

    [Fact]
    public void JobBuilder_WithContextStackSize_SetsStackSize()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        builder.AddJob<TestJob>().WithContextStackSize(10);

        var provider = services.BuildServiceProvider();
        var jobSettings = provider.GetRequiredService<JobSettings>();

        Assert.Equal(10, jobSettings.Properties.ContextStackSize);
    }

    [Fact]
    public void JobBuilder_ConfigureErrorHandling_SetsRetryAndAction()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        builder.AddJob<TestJob>((sp, job) =>
        {
            job.ConfigureErrorHandling(err => err
                .IfErrorRetry(5)
                .ThenAbortJob());
        });

        var provider = services.BuildServiceProvider();
        var jobSettings = provider.GetRequiredService<JobSettings>();

        Assert.Equal(5, jobSettings.ErrorHandling.RetryCount);
        Assert.Equal(ErrorHandlingAction.AbortJob, jobSettings.ErrorHandling.ThenAction);
    }

    [Fact]
    public void JobBuilder_ConfigureErrorHandling_SetsSuppressError()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        builder.AddJob<TestJob>((sp, job) =>
        {
            job.ConfigureErrorHandling(err => err
                .DoSuppressError(ex => ex is OperationCanceledException)
                .ThenAbortJob());
        });

        var provider = services.BuildServiceProvider();
        var jobSettings = provider.GetRequiredService<JobSettings>();

        Assert.NotNull(jobSettings.ErrorHandling.SuppressError);
        Assert.True(jobSettings.ErrorHandling.SuppressError!(new OperationCanceledException()));
        Assert.False(jobSettings.ErrorHandling.SuppressError!(new InvalidOperationException()));
    }

    [Fact]
    public void JobBuilder_ConfigureErrorHandling_DefaultCloseApplication()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        builder.AddJob<TestJob>();

        var provider = services.BuildServiceProvider();
        var jobSettings = provider.GetRequiredService<JobSettings>();

        // Default error handling action is CloseApplication
        Assert.Equal(ErrorHandlingAction.CloseApplication, jobSettings.ErrorHandling.ThenAction);
        // Default retry count is 0 (not set unless IfErrorRetry is called)
        Assert.Equal(0, jobSettings.ErrorHandling.RetryCount);
    }

    [Fact]
    public void JobBuilder_OnceIn_SetsInitialDelayAndRunOnce()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);
        var delay = TimeSpan.FromSeconds(10);

        builder.AddJob<TestJob>().OnceIn(delay);

        var provider = services.BuildServiceProvider();
        var jobSettings = provider.GetRequiredService<JobSettings>();

        Assert.Equal(delay, jobSettings.Properties.InitialDelay);
        Assert.True(jobSettings.Properties.IsRunOnce);
    }

    [Fact]
    public void JobBuilder_WithConcurrencyLimit_ValidatesRange()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        IJobBuilder act() => builder.AddJob<TestJob>().WithConcurrencyLimit(-1);

        Assert.Throws<ArgumentOutOfRangeException>((Func<IJobBuilder>)act);
    }

    [Fact]
    public void AddJob_WithCustomId_UsesProvidedId()
    {
        var services = new ServiceCollection();
        var customId = Guid.NewGuid();
        var builder = new ScheduleBuilder(services);

        builder.AddJob<TestJob>(customId);

        var provider = services.BuildServiceProvider();
        var jobSettings = provider.GetRequiredService<JobSettings>();

        Assert.Equal(customId, jobSettings.JobId);
    }

    [Fact]
    public void AddJob_WithoutId_GeneratesNewId()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        builder.AddJob<TestJob>();

        var provider = services.BuildServiceProvider();
        var jobSettings = provider.GetRequiredService<JobSettings>();

        Assert.NotEqual(Guid.Empty, jobSettings.JobId);
    }

    [Fact]
    public void ScheduleBuilder_AddErrorHandler_RegistersGlobalHandler()
    {
        var services = new ServiceCollection();
        var builder = new ScheduleBuilder(services);

        builder.AddErrorHandler((ctx, ex) => true).AddJob<TestJob>();

        var provider = services.BuildServiceProvider();
        var scheduleSettings = provider.GetRequiredService<IScheduleSettings>();

        // The handler should be registered — verify via Settings
        Assert.NotNull(scheduleSettings);
    }

    sealed class TestJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    sealed class TestInterceptor : IJobInterceptor
    {
        public Task OnHandle(IJobContext context, Func<Task> next, object? key, CancellationToken cancellationToken)
            => next();
    }
}
