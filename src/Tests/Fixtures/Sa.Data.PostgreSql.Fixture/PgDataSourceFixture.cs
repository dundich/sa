namespace Sa.Data.PostgreSql.Fixture;


public class PgDataSourceFixture<TSub> : PostgreSqlFixture<TSub, PostgreSqlFixtureSettings>
     where TSub : notnull
{
    private readonly Lazy<IPgDataSource> _dataSource;

    protected PgDataSourceFixture(PostgreSqlFixtureSettings? settings)
        : base(settings ?? PostgreSqlFixtureSettings.Instance)
            => _dataSource = new(()
                => IPgDataSource.Create(this.ConnectionString));

    public PgDataSourceFixture() : this(null) { }

    public IPgDataSource DataSource => _dataSource.Value;

    public async override ValueTask DisposeAsync()
    {
        if (_dataSource.IsValueCreated && _dataSource.Value is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    public async Task CheckTable(string tablename)
    {
        await DataSource.ExecuteNonQuery($"SELECT '{tablename}'::regclass;");
    }
}


public class PgDataSourceFixture : PgDataSourceFixture<IPgDataSource>
{

}