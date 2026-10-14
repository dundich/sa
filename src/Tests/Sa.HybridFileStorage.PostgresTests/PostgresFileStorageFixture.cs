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
        // The registration key is the table name: each fixture builds its own service collection,
        // and the keyed storage is also aliased as the unkeyed IFileStorage this fixture resolves.
        Services.AddSaPostgreSqlFileStorageChained(tableName, b => b.Options(ob => ob.Configure(opts =>
            {
                opts.TableName = tableName;
                configure?.Invoke(opts);
            })))
            .AddDataSource(b => b.Options(ob => ob.Configure(o => o.ConnectionString = ConnectionString)));
    }
}
