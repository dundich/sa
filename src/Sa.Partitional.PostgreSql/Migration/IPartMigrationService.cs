
namespace Sa.Partitional.PostgreSql;

public interface IPartMigrationService
{
    CancellationToken OnMigrated { get; }
    Task<int> Migrate(CancellationToken cancellationToken = default);
    Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default);

    Task<bool> WaitMigration(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (OnMigrated.IsCancellationRequested) return Task.FromResult(true);

        var tcs = new TaskCompletionSource();
        OnMigrated.Register(() => tcs.SetResult());
        return Task.Run(() => Task.WaitAny(tcs.Task, Task.Delay(timeout, cancellationToken)) == 0);
    }
}
