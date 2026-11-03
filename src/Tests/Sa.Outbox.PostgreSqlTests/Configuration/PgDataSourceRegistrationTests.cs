using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;

namespace Sa.Outbox.PostgreSqlTests.Configuration;

/// <summary>
/// Who owns the provider's data source. A repeated registration of the same outbox key must not stack
/// a second data source, while a key another module already registered is either borrowed (a bare
/// <c>WithDataSource()</c>) or refused with a clear error (an explicit connection string) — never
/// silently ignored.
/// </summary>
public class PgDataSourceRegistrationTests
{
    [Fact]
    public void SameKeyRegisteredTwice_KeepsOneOwnedDataSource()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingPostgreSql(Configure, scopeKey: "dup");
        services.AddSaOutboxUsingPostgreSql(Configure, scopeKey: "dup");

        Assert.Single(services, d => d.ServiceType == typeof(IPgDataSource) && d.IsKeyedService);
        Assert.Single(services, d => d.ServiceType == typeof(PgOutboxDataSourceMarker));

        static void Configure(IPgOutboxConfiguration pg)
            => pg.WithDataSource(ds => ds.Options(options => options.Configure(
                o => o.ConnectionString = "Host=localhost;Database=outbox")));
    }

    [Fact]
    public void AnotherModuleRegisteredTheKey_ABareRequestBorrowsIt()
    {
        var services = new ServiceCollection();

        // E.g. an application repository or another keyed module configured the shared key first.
        services.AddSaPostgreSqlDataSource("shared");

        services.AddSaOutboxUsingPostgreSql(cfg => cfg.WithDataSource(), scopeKey: "shared");

        Assert.Single(services, d => d.ServiceType == typeof(IPgDataSource) && d.IsKeyedService);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(PgOutboxDataSourceMarker));
    }

    [Fact]
    public void AnotherModuleRegisteredTheKey_AnExplicitConnectionStringIsApplied()
    {
        var services = new ServiceCollection();

        // A bare registration carries no connection string, so the outbox call may still provide
        // one. It used to be skipped because "a keyed data source exists", leaving the key without
        // a connection string.
        services.AddSaPostgreSqlDataSource("shared");

        services.AddSaOutboxUsingPostgreSql(
            cfg => cfg.WithDataSource(ds => ds.Options(options => options.Configure(
                o => o.ConnectionString = "Host=outbox-db"))),
            scopeKey: "shared");

        using ServiceProvider provider = services.BuildServiceProvider();

        PgDataSourceOptions options = provider
            .GetRequiredService<IOptionsMonitor<PgDataSourceOptions>>()
            .Get("shared");

        Assert.Equal("Host=outbox-db", options.ConnectionString);
    }

    [Fact]
    public void AnotherModuleConfiguredTheKey_AnExplicitConnectionStringFailsInsteadOfBeingIgnored()
    {
        var services = new ServiceCollection();

        services.AddSaPostgreSqlDataSource("shared", ds => ds.Options(options => options.Configure(
            o => o.ConnectionString = "Host=other-db")));

        // Two configuring calls for one key would stack two Configure callbacks; the data source
        // guard reports it instead of the outbox silently keeping the other connection.
        Assert.Throws<InvalidOperationException>(() => services.AddSaOutboxUsingPostgreSql(
            cfg => cfg.WithDataSource(ds => ds.Options(options => options.Configure(
                o => o.ConnectionString = "Host=outbox-db"))),
            scopeKey: "shared"));
    }
}
