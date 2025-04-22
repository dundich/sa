using Sa.Data.PostgreSql.Fixture;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Postgres;

namespace Sa.HybridFileStorage.PostgresTests;


public class PostgresFileStorageFixturee : PgDataSourceFixture<IFileStorage>
{
    protected PostgresFileStorageFixturee(string tableName)
    {
        Services.AddPostgresHybridFileStorage(cfg => cfg
            .AddDataSource(b => b.WithConnectionString(sp => ConnectionString))
            .WithTableName(tableName)
        );
    }
}
