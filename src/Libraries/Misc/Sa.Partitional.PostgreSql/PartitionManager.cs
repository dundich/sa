using Sa.Partitional.PostgreSql.Cache;

namespace Sa.Partitional.PostgreSql;


internal sealed class PartitionManager(IPartCache cache, IMigrationService migrationService) : IPartitionManager
{
    public Task EnsurePartsAsync(
        string tableName,
        DateTimeOffset periodStart,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default)
        => cache.EnsureCacheAsync(tableName, periodStart, partValues, cancellationToken);

    public Task<int> MigrateAsync(DateTimeOffset[] dates, CancellationToken cancellationToken = default)
        => migrationService.MigrateAsync(dates, cancellationToken);

    public Task<int> MigrateAsync(CancellationToken cancellationToken = default)
        => migrationService.MigrateAsync(cancellationToken);
}
