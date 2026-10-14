using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sa.Data.PostgreSql;

namespace Sa.Data.PostgreSqlTests;

/// <summary>
/// <see cref="PgDataSourceResolver"/>: the last-resort fallback that lets a consumer ride on an
/// application-registered <see cref="NpgsqlDataSource"/> when neither a keyed nor the default
/// <see cref="IPgDataSource"/> exists. No database is started — creating an
/// <see cref="NpgsqlDataSource"/> does not open a connection, and <see cref="PgDataSource"/>
/// (the wrapper <see cref="IPgDataSource.Create(NpgsqlDataSource)"/> returns) borrows the pool
/// without owning it.
/// </summary>
public sealed class PgDataSourceResolverTests
{
    private const string ValidConnectionString = "Host=127.0.0.1;Database=postgres;Username=u;Password=p";

    private static string WithSearchPath(string searchPath) => $"{ValidConnectionString};Search Path={searchPath}";

    // ---------- fallback to a registered NpgsqlDataSource ----------

    [Fact]
    public void Resolve_NoIPgDataSource_BuildsFromTheUnkeyedNpgsqlDataSource()
    {
        var borrowed = NpgsqlDataSource.Create(WithSearchPath("storage"));
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<NpgsqlDataSource>(borrowed);

            using var provider = services.BuildServiceProvider();

            var dataSource = PgDataSourceResolver.Resolve(provider);

            Assert.Equal("storage", dataSource.GetSearchPath());
        }
        finally
        {
            // The wrapper does not own the borrowed source (and never disposes it), so this test does.
            borrowed.Dispose();
        }
    }

    [Fact]
    public void Resolve_WithAKey_FallsBackToTheUnkeyedNpgsqlDataSourceToo()
    {
        // The keyed consumer still falls back to the shared unkeyed source: a module with no named
        // data source and no default IPgDataSource should reuse the application's NpgsqlDataSource.
        var borrowed = NpgsqlDataSource.Create(WithSearchPath("storage"));
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<NpgsqlDataSource>(borrowed);

            using var provider = services.BuildServiceProvider();

            Assert.Equal("storage", PgDataSourceResolver.Resolve(provider, "module").GetSearchPath());
        }
        finally
        {
            borrowed.Dispose();
        }
    }

    [Fact]
    public void Resolve_AKeyedNpgsqlDataSource_WinsOverTheUnkeyedOne()
    {
        var keyed = NpgsqlDataSource.Create(WithSearchPath("keyed_schema"));
        var shared = NpgsqlDataSource.Create(WithSearchPath("shared_schema"));
        try
        {
            var services = new ServiceCollection();
            services.AddKeyedSingleton<NpgsqlDataSource>("db1", keyed);
            services.AddSingleton<NpgsqlDataSource>(shared);

            using var provider = services.BuildServiceProvider();

            Assert.Equal("keyed_schema", PgDataSourceResolver.Resolve(provider, "db1").GetSearchPath());
            Assert.Equal("shared_schema", PgDataSourceResolver.Resolve(provider, "db2").GetSearchPath());
        }
        finally
        {
            keyed.Dispose();
            shared.Dispose();
        }
    }

    // ---------- IPgDataSource still wins ----------

    [Fact]
    public void Resolve_AnIPgDataSource_WinsOverANpgsqlDataSource()
    {
        var borrowed = NpgsqlDataSource.Create(WithSearchPath("from_npgsql"));
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<NpgsqlDataSource>(borrowed);
            services.AddSaPostgreSqlDataSource(
                o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("from_sa"))));

            using var provider = services.BuildServiceProvider();

            Assert.Equal("from_sa", PgDataSourceResolver.Resolve(provider).GetSearchPath());
        }
        finally
        {
            borrowed.Dispose();
        }
    }

    [Fact]
    public void Resolve_AKeyedIPgDataSource_WinsOverTheDefaultOne()
    {
        var services = new ServiceCollection();
        services.AddSaPostgreSqlDataSource(
            o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("default_schema"))));
        services.AddSaPostgreSqlDataSource("own",
            o => o.Options(ob => ob.Configure(x => x.ConnectionString = WithSearchPath("own_schema"))));

        using var provider = services.BuildServiceProvider();

        Assert.Equal("own_schema", PgDataSourceResolver.Resolve(provider, "own").GetSearchPath());
    }

    // ---------- nothing to connect to ----------

    [Fact]
    public void Resolve_NoSourceAtAll_Throws()
    {
        var services = new ServiceCollection();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => PgDataSourceResolver.Resolve(provider, "any"));
        Assert.Contains("No IPgDataSource is registered", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no NpgsqlDataSource is registered", ex.Message, StringComparison.Ordinal);
    }
}