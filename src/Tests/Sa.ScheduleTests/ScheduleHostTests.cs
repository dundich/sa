using Sa.Schedule;
using Sa.Schedule.Engine;

namespace Sa.ScheduleTests;

/// <summary>
/// The hosted-service shell must respect the host's shutdown deadline: each job's
/// <c>Stop()</c> is bounded by its own shutdown timeout, but the host's token may be
/// shorter and used to be ignored entirely.
/// </summary>
public sealed class ScheduleHostTests
{
    [Fact]
    public async Task StopAsync_HonoursTheHostToken_InsteadOfWaitingForStopToFinish()
    {
        var scheduler = new NeverStoppingScheduler();
        var host = new ScheduleHost(scheduler);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The scheduler's Stop() never completes; with an already-cancelled host token
        // the shell must return instead of hanging.
        await host.StopAsync(cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private sealed class NeverStoppingScheduler : IScheduler
    {
        private readonly TaskCompletionSource _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IScheduleSettings Settings => throw new NotSupportedException();

        public IReadOnlyCollection<IJobScheduler> Jobs => [];

        public Task<int> Start(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<int> Restart(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task Stop() => _never.Task;
    }
}
