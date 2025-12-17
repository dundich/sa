using Sa.Classes;

namespace Sa.Partitional.PostgreSql;

public interface IPartTableMigrationSupport
{
    Task<StrOrNum[][]> GetParts(CancellationToken cancellationToken);
}
