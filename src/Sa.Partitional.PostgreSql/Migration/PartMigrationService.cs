using Sa.Extensions;

namespace Sa.Partitional.PostgreSql.Migration;

internal sealed class PartMigrationService(
    IPartRepository repository
    , TimeProvider timeProvider
    , MigrationScheduleSettings settings) : IMigrationService, IDisposable
{
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken OnMigrated => _cts.Token;

    public void Dispose()
    {
        _cts.Dispose();
        _migrationLock.Dispose();
    }

    public Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default)
       => repository.Migrate(dates, cancellationToken);

    public async Task<int> Migrate(CancellationToken cancellationToken = default)
    {
        // Acquire exclusive lock with timeout to prevent indefinite spinning.
        if (!await _migrationLock.WaitAsync(settings.WaitMigrationTimeout, cancellationToken).ConfigureAwait(false))
            return -1;

        try
        {
            DateTimeOffset now = timeProvider.GetUtcNow().StartOfDay();
            DateTimeOffset[] dates = [.. Enumerable
                .Range(0, settings.ForwardDays)
                .Select(i => now.AddDays(i))];

            int result = await repository.Migrate(dates, cancellationToken).ConfigureAwait(false);
            _cts.Cancel();
            return result;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        finally
        {
            _migrationLock.Release();
        }
    }
}
