using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule;
using Sa.Schedule.Engine;
using Sa.Schedule.Settings;

namespace Sa.ScheduleTests;

public class JobControllerTests
{
    [Fact]
    public void PauseAndResume_WorkCorrectly()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var controller = CreateController(settings);

        Assert.False(controller.IsPaused);

        controller.Pause();
        Assert.True(controller.IsPaused);

        controller.Resume();
        Assert.False(controller.IsPaused);
    }

    [Fact]
    public void Shutdown_IsIdempotent()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var controller = CreateController(settings);

        // Shutdown without Start should be safe
        controller.Shutdown();

        // Subsequent calls should be no-op
        controller.Shutdown();
        controller.Pause();
        controller.Resume();
        Assert.True(true);
    }

    [Fact]
    public void Index_ReturnsConstructorValue()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var controller = CreateController(settings, index: 42);

        Assert.Equal(42, controller.Index);
    }

    [Fact]
    public void Pause_WhileAlreadyPaused_IsNoop()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var controller = CreateController(settings);

        controller.Pause();
        controller.Pause(); // Double pause — should not throw or deadlock

        Assert.True(controller.IsPaused);
    }

    [Fact]
    public void Resume_WhileNotPaused_IsNoop()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var controller = CreateController(settings);

        controller.Resume(); // Double resume — should not throw

        Assert.False(controller.IsPaused);
    }

    [Fact]
    public void Dispose_CallsShutdown()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var controller = CreateController(settings);

        controller.Dispose(); // Should not throw

        // After dispose, operations should be safe no-ops
        controller.Pause();
        controller.Resume();
        controller.Shutdown();
        Assert.True(true);
    }

    [Fact]
    public void Index_IsImmutable()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var controllerA = CreateController(settings, index: 0);
        var controllerB = CreateController(settings, index: 99);

        Assert.Equal(0, controllerA.Index);
        Assert.Equal(99, controllerB.Index);
    }

    [Fact]
    public void WithMaxConcurrency_RejectsZero()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var ex = Record.Exception(() => settings.Properties.WithMaxConcurrencyLimit(0));
        Assert.IsType<ArgumentOutOfRangeException>(ex);
    }

    [Fact]
    public void WithConcurrencyLimit_RejectsNegative()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var ex = Record.Exception(() => settings.Properties.WithConcurrencyLimit(-1));
        Assert.IsType<ArgumentOutOfRangeException>(ex);
    }

    private static JobController CreateController(IJobSettings settings, int index = 0, TimeProvider? timeProvider = null)
    {
        var scopeFactory = new MockScopeFactory();
        return new JobController(
            index,
            settings,
            new InterceptorSettings([]),
            scopeFactory,
            timeProvider ?? TimeProvider.System);
    }

    sealed class TestJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    sealed class MockScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScope _scope = new MockScope();
        public IServiceScope CreateScope() => _scope;
    }

    sealed class MockScope : IServiceScope
    {
        public IServiceProvider ServiceProvider => new MockServiceProvider();
        public void Dispose() { }
    }

    sealed class MockServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
