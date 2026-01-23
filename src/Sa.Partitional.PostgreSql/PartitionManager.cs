using Sa.Partitional.PostgreSql.Cache;
using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;


internal sealed class PartitionManager(IPartCache cache, IMigrationService migrationService) : IPartitionManager
{
    public Task<bool> EnsureParts(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default)
        => cache.EnsureCache(tableName, date, partValues, cancellationToken);

    public Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default)
        => migrationService.Migrate(dates, cancellationToken);

    public Task<int> Migrate(CancellationToken cancellationToken = default)
        => migrationService.Migrate(cancellationToken);
}
