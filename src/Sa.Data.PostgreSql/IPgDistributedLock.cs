namespace Sa.Data.PostgreSql;

public interface IPgDistributedLock
{
    Task<bool> TryExecuteInDistributedLock(long lockId, Func<CancellationToken, Task> exclusiveLockTask, CancellationToken cancellationToken);
}
