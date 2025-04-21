using Sa.Data.PostgreSql.Fixture;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.PostgresFileStorage;

namespace Sa.HybridFileStorage.PostgresFileStorageTests;


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
