using Sa.Data.PostgreSql.Fixture;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.PostgresFileStorage.Tests;


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
