using Sa.Data.PostgreSql;
using Sa.Data.PostgreSql.Fixture;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.PostgresFileStorage.Tests;


public class PostgresFileStorageFixturee : PgDataSourceFixture<IFileStorage>
{
    protected PostgresFileStorageFixturee(PostgresStorageOption option)
    {
        Services.AddPgDataSource(b => b.WithConnectionString(sp => ConnectionString));

        Options = option;
        Services.AddPostgresHybridFileStorage(option);
    }

    public PostgresStorageOption Options { get; private set; }
}
