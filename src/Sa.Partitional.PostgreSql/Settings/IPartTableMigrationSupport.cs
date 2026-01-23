using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

public interface IPartTableMigrationSupport
{
    Task<StrOrNum[][]> GetParts(CancellationToken cancellationToken);
}
