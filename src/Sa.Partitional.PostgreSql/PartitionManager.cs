using Sa.Classes;
using Sa.Partitional.PostgreSql.Cache;

namespace Sa.Partitional.PostgreSql;


internal class PartitionManager(IPartCache cache, IPartMigrationService migrationService) : IPartitionManager
{
    public Task<bool> EnsureParts(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default)
    {
        return cache.EnsureCache(tableName, date, partValues, cancellationToken);
    }

    public Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default)
    {
        return migrationService.Migrate(dates, cancellationToken);
    }

    public Task<int> Migrate(CancellationToken cancellationToken = default)
    {
        return migrationService.Migrate(cancellationToken);
    }
}
