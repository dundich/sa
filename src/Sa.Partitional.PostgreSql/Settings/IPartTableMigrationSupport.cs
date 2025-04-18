using Sa.Classes;

namespace Sa.Partitional.PostgreSql;

public interface IPartTableMigrationSupport
{
    Task<StrOrNum[][]> GetPartValues(CancellationToken cancellationToken);
}
