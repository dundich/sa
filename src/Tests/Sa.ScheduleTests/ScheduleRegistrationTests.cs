using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule;

namespace Sa.ScheduleTests;

/// <summary>
/// Composition of <see cref="Setup.AddSaSchedule"/> across repeated calls. Libraries that
/// contribute jobs call it more than once in a single container — <c>AddSaPartitional</c>
/// registers migration + cleanup, <c>AddDeliveryJob</c> runs per consumer group — so the
/// global settings must live in the container rather than on the builder that the first
/// <c>TryAdd</c> happened to keep.
/// </summary>
public class ScheduleRegistrationTests
{
    [Fact]
    public void AddErrorHandler_FromSecondCall_IsNotLost()
    {
        var services = new ServiceCollection();

        // The first builder wins the IScheduleSettings registration; a handler handed to the
        // second one used to be stored on a field nobody read.
        services.AddSaSchedule(b => b.AddErrorHandler((_, _) => false));
        services.AddSaSchedule(b => b.AddErrorHandler((_, _) => true));

        var settings = services.BuildServiceProvider().GetRequiredService<IScheduleSettings>();

        Assert.NotNull(settings.HandleError);
        Assert.True(settings.HandleError!(null!, new InvalidOperationException()));
    }

    [Fact]
    public void AddErrorHandler_Composes_AnyHandlerConsumes()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule(b => b.AddErrorHandler((_, ex) => ex is TimeoutException));
        services.AddSaSchedule(b => b.AddErrorHandler((_, _) => true));

        var settings = services.BuildServiceProvider().GetRequiredService<IScheduleSettings>();

        Assert.True(settings.HandleError!(null!, new TimeoutException()));
        // Neither handler claims a non-timeout — the per-job policy must still decide.
        Assert.True(settings.HandleError!(null!, new InvalidOperationException()));
    }

    [Fact]
    public void AddErrorHandler_AllDecline_LeavesErrorToJobPolicy()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule(b => b.AddErrorHandler((_, _) => false));
        services.AddSaSchedule(b => b.AddErrorHandler((_, _) => false));

        var settings = services.BuildServiceProvider().GetRequiredService<IScheduleSettings>();

        Assert.NotNull(settings.HandleError);
        Assert.False(settings.HandleError!(null!, new InvalidOperationException()));
    }

    [Fact]
    public void NoErrorHandlerRegistered_LeavesHandleErrorNull()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule(b => b.AddJob<TestJob>());

        var settings = services.BuildServiceProvider().GetRequiredService<IScheduleSettings>();

        Assert.Null(settings.HandleError);
    }

    [Fact]
    public void UseHostedService_FromSecondCall_IsNotLost()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule(b => b.AddJob<TestJob>());
        services.AddSaSchedule(b => b.UseHostedService());

        var settings = services.BuildServiceProvider().GetRequiredService<IScheduleSettings>();

        Assert.True(settings.IsHostedService);
    }

    [Fact]
    public void UseHostedService_FromSeveralCalls_RegistersSingleHost()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule(b => b.UseHostedService());
        services.AddSaSchedule(b => b.UseHostedService());

        var hosted = services
            .BuildServiceProvider()
            .GetServices<Microsoft.Extensions.Hosting.IHostedService>();

        Assert.Single(hosted);
    }

    [Fact]
    public void AddSaSchedule_WithoutConfigure_StillResolvesScheduler()
    {
        // The settings used to be registered as a side effect of `new ScheduleBuilder(...)`,
        // so a call with no configure left the scheduler unresolvable.
        var services = new ServiceCollection();

        services.AddSaSchedule();

        var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IScheduleSettings>());
        Assert.NotNull(provider.GetRequiredService<IScheduler>());
        Assert.Empty(provider.GetRequiredService<IScheduler>().Jobs);
    }

    [Fact]
    public void AddSaSchedule_MultipleCalls_AccumulateJobs()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule(b => b.AddJob<TestJob>());
        services.AddSaSchedule(b => b.AddJob<TestJob>());

        var scheduler = services.BuildServiceProvider().GetRequiredService<IScheduler>();

        Assert.Equal(2, scheduler.Jobs.Count);
    }

    [Fact]
    public void AddSaSchedule_NullServices_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddSaSchedule());

    [Fact]
    public void AddErrorHandler_NullHandler_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(
            () => services.AddSaSchedule(b => b.AddErrorHandler(null!)));
    }

    sealed class TestJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
