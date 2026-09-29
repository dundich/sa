using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Classes;
using Sa.Partitional.PostgreSql.Migration;

namespace Sa.Partitional.PostgreSqlTests.Migration;

public class PartMigrationServiceTests
{
    /// <summary>
    /// Records what the service handed down and lets the test control when a run finishes.
    /// </summary>
    private sealed class FakeRepository : IPartRepository
    {
        public Func<CancellationToken, Task<int>> OnMigrate { get; set; } = _ => Task.FromResult(1);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// The token the service passed into the repository - the same token that reaches user
        /// IPartTableMigrationSupport.GetParts code.
        /// </summary>
        public CancellationToken RepositoryToken { get; private set; }

        public Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default)
        {
            RepositoryToken = cancellationToken;
            Started.TrySetResult();

            return OnMigrate(cancellationToken);
        }

        public Task<int> Migrate(DateTimeOffset[] dates, Func<string, Task<StrOrNum[][]>> resolve, CancellationToken cancellationToken = default)
            => Migrate(dates, cancellationToken);

        public Task<int> CreatePart(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<List<PartByRangeInfo>> GetPartsFromDate(string tableName, DateTimeOffset fromDate, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<List<PartByRangeInfo>> GetPartsToDate(string tableName, DateTimeOffset toDate, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> DropPartsToDate(string tableName, DateTimeOffset toDate, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static PartMigrationService Create(
        FakeRepository repository,
        TimeSpan? runTimeout = null,
        TimeSpan? waitTimeout = null,
        ILogger<PartMigrationService>? logger = null)
        => new(
            repository,
            TimeProvider.System,
            new MigrationScheduleSettings
            {
                ForwardDays = 1,
                RunTimeout = runTimeout ?? TimeSpan.FromSeconds(10),
                WaitMigrationTimeout = waitTimeout ?? TimeSpan.FromMilliseconds(200)
            },
            logger ?? NullLogger<PartMigrationService>.Instance);

    [Fact]
    public async Task Migrate_ReturnsTheStatementCount_AndSignalsOnMigrated()
    {
        FakeRepository repository = new();
        using PartMigrationService service = Create(repository);

        int result = await service.Migrate(TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        Assert.True(service.OnMigrated.IsCancellationRequested);

        // Already migrated, so waiting is a no-op.
        IMigrationService sut = service;
        Assert.True(await sut.WaitMigration(TimeSpan.Zero, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Migrate_GivesTheRepositoryItsOwnToken_NotTheCallers()
    {
        FakeRepository repository = new();
        using PartMigrationService service = Create(repository);
        using CancellationTokenSource callerCts = new();

        await service.Migrate(callerCts.Token);

        Assert.True(repository.RepositoryToken.CanBeCanceled);
        Assert.NotEqual(callerCts.Token, repository.RepositoryToken);
    }

    [Fact]
    public async Task Migrate_IsNotInterruptedByTheCallersToken_OnceTheLockIsTaken()
    {
        FakeRepository repository = new();
        using PartMigrationService service = Create(repository);
        using CancellationTokenSource callerCts = new();

        Task<int> run = service.Migrate(callerCts.Token);

        // Wait until the run is really under way, then cancel the caller: the DDL is idempotent,
        // so a started run has to reach the end instead of dying half-way.
        await repository.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await callerCts.CancelAsync();

        Assert.Equal(1, await run);
    }

    [Fact]
    public async Task Migrate_ReturnsMinusOne_WhenTheOwnDeadlineElapses()
    {
        FakeRepository repository = new()
        {
            OnMigrate = async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 1;
            }
        };

        using PartMigrationService service = Create(repository, runTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(-1, await service.Migrate(TestContext.Current.CancellationToken));
        Assert.False(service.OnMigrated.IsCancellationRequested);
    }

    [Fact]
    public async Task Migrate_SkipsTheTick_WhenTheLockIsBusy()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeRepository repository = new()
        {
            OnMigrate = async ct =>
            {
                await gate.Task.WaitAsync(ct);
                return 1;
            }
        };

        using PartMigrationService service = Create(repository);

        Task<int> first = service.Migrate(TestContext.Current.CancellationToken);
        await repository.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The second tick is skipped, not queued behind the first one.
        Assert.Equal(-1, await service.Migrate(TestContext.Current.CancellationToken));

        gate.SetResult();

        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task Migrate_WithDates_IsSerializedAgainstTheBackgroundRun()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeRepository repository = new()
        {
            OnMigrate = async ct =>
            {
                await gate.Task.WaitAsync(ct);
                return 1;
            }
        };

        using PartMigrationService service = Create(repository);

        Task<int> manual = service.Migrate([DateTimeOffset.UnixEpoch], TestContext.Current.CancellationToken);
        await repository.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The manual overload used to bypass the lock entirely and run beside the background job.
        Assert.Equal(-1, await service.Migrate(TestContext.Current.CancellationToken));

        gate.SetResult();

        Assert.Equal(1, await manual);
    }

    [Fact]
    public async Task WaitMigration_ReturnsFalse_WhenTheDeadlineElapses()
    {
        FakeRepository repository = new();
        using PartMigrationService service = Create(repository);

        IMigrationService sut = service;

        bool completed = await sut.WaitMigration(
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken);

        Assert.False(completed);
    }

    [Fact]
    public async Task WaitMigration_ReturnsTrue_WhenMigrationCompletesWhileWaiting()
    {
        FakeRepository repository = new();
        using PartMigrationService service = Create(repository);

        IMigrationService sut = service;

        Task<bool> waiting = sut.WaitMigration(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await service.Migrate(TestContext.Current.CancellationToken);

        Assert.True(await waiting);
    }

    /// <summary>
    /// Captures the messages the service logs, so a log-only fix can be asserted on.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages) return [.. _messages];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages) _messages.Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task Migrate_WhenTheOwnDeadlineElapses_LogsItAsElapsed()
    {
        FakeRepository repository = new()
        {
            OnMigrate = async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 1;
            }
        };

        RecordingLogger<PartMigrationService> logger = new();

        using PartMigrationService service = Create(
            repository,
            runTimeout: TimeSpan.FromMilliseconds(200),
            logger: logger);

        // The manual overload runs on a *linked* token, so the exception Npgsql raises carries that
        // linked token rather than the deadline's - comparing tokens reported the elapsed deadline
        // as "not the deadline", hiding the real cause in the log.
        Assert.Equal(-1, await service.Migrate([DateTimeOffset.UnixEpoch], TestContext.Current.CancellationToken));

        Assert.Contains(logger.Messages, m => m.Contains("DeadlineElapsed: True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migrate_WhenTheCallerCancels_LogsTheDeadlineAsNotElapsed()
    {
        FakeRepository repository = new()
        {
            OnMigrate = async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 1;
            }
        };

        RecordingLogger<PartMigrationService> logger = new();
        using PartMigrationService service = Create(repository, logger: logger);
        using CancellationTokenSource callerCts = new(TimeSpan.FromMilliseconds(200));

        Assert.Equal(-1, await service.Migrate([DateTimeOffset.UnixEpoch], callerCts.Token));

        // The opposite direction: the run gave up on the caller's request, not on its own deadline.
        Assert.Contains(logger.Messages, m => m.Contains("DeadlineElapsed: False", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migrate_FinishesNormally_WhenTheServiceIsDisposedWhileTheRunIsInFlight()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeRepository repository = new()
        {
            OnMigrate = async ct =>
            {
                await gate.Task.WaitAsync(ct);
                return 1;
            }
        };

        PartMigrationService service = Create(repository);

        Task<int> run = service.Migrate([DateTimeOffset.UnixEpoch], TestContext.Current.CancellationToken);
        await repository.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The host disposes the service on its way out while a run still holds the lock - the normal
        // end of a migration started just before shutdown.
        service.Dispose();

        gate.SetResult();

        // Signalling the completion and releasing the lock both touch disposed state; neither may
        // turn a run that actually finished into a reported failure.
        Assert.Equal(1, await run);
    }
}
