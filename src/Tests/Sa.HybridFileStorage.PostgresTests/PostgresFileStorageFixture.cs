using Sa.Data.PostgreSql.Fixture;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Postgres;

namespace Sa.HybridFileStorage.PostgresTests;


public class PostgresFileStorageFixture : PgDataSourceFixture<IFileStorage>
{
    protected PostgresFileStorageFixture(
        string tableName,
        Action<PostgresFileStorageOptions>? configure = null)
    {
        Services.AddSaPostgreSqlFileStorageChained(opts =>
            {
                opts.TableName = tableName;
                configure?.Invoke(opts);
            })
            .AddDataSource(b => b.WithConnectionString(_ => ConnectionString));
    }
}
