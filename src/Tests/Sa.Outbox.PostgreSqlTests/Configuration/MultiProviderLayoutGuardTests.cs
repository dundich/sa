using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.Services.Plug;

namespace Sa.Outbox.PostgreSqlTests.Configuration;

/// <summary>
/// The PostgreSQL multi-provider layout guard: two providers on the same tables would consume the
/// same rows twice, so the host start fails; distinct schemas or distinct databases pass.
/// </summary>
public class MultiProviderLayoutGuardTests
{
    private const string ConnectionString = "Host=localhost;Port=5432;Database=outbox;Username=u;Password=p";

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(
        (string Key, string ConnectionString, string Schema) a,
        (string Key, string ConnectionString, string Schema)? b = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddSaOutboxUsingPostgreSql(
            Configure(a.ConnectionString, a.Schema),
            configureOutbox: null,
            a.Key);

        if (b is { } other)
        {
            services.AddSaOutboxUsingPostgreSql(
                Configure(other.ConnectionString, other.Schema),
                configureOutbox: null,
                other.Key);
        }

        return services.BuildServiceProvider();

        static Action<IPgOutboxConfiguration> Configure(string connectionString, string schema)
            => pg => pg
                .WithDataSource(ds => ds.Options(options => options.Configure(
                    o => o.ConnectionString = connectionString)))
                .WithOutboxSettings((_, settings) => settings.TableSettings.WithSchema(schema));
    }

    private static async Task StartGuard(ServiceProvider sp)
    {
        PgMultiProviderGuard guard = Assert.Single(
            sp.GetServices<IHostedService>().OfType<PgMultiProviderGuard>());

        await guard.StartAsync(TestToken);
    }

    [Fact]
    public async Task SameDatabaseAndSchema_FailsTheStart()
    {
        using ServiceProvider sp = Build(("a", ConnectionString, "outbox"), ("b", ConnectionString, "outbox"));

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() => StartGuard(sp));

        Assert.Contains("deliver every message twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DifferentSchemas_AreIndependent()
    {
        using ServiceProvider sp = Build(("a", ConnectionString, "outbox_a"), ("b", ConnectionString, "outbox_b"));

        await StartGuard(sp); // does not throw
    }

    [Fact]
    public async Task DifferentDatabases_AreIndependent()
    {
        using ServiceProvider sp = Build(
            ("a", "Host=localhost;Port=5432;Database=outbox_a;Username=u;Password=p", "outbox"),
            ("b", "Host=localhost;Port=5432;Database=outbox_b;Username=u;Password=p", "outbox"));

        await StartGuard(sp); // does not throw
    }

    [Fact]
    public async Task SingleProvider_Passes()
    {
        using ServiceProvider sp = Build(("outbox", ConnectionString, "outbox"));

        await StartGuard(sp); // does not throw
    }
}
