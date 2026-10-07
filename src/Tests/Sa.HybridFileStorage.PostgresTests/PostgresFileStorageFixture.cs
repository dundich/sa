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
        Services.AddSaPostgreSqlFileStorageChained(b => b.Options(ob => ob.Configure(opts =>
            {
                opts.TableName = tableName;
                configure?.Invoke(opts);
            })))
            .AddDataSource(b => b.Options(ob => ob.Configure(o => o.ConnectionString = ConnectionString)));
    }
}
