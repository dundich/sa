using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sa.Configuration;
using Sa.Configuration.Clients;
using Sa.Configuration.PostgreSql;
using Sa.Data.PostgreSql;
using Sa.Data.PostgreSql.Fixture;

namespace Sa.Configuration.PostgreSqlTests;

/// <summary>
/// The client mapping of the PostgreSQL source: a three-column query that returns every client at
/// once, the constant-client form for a filtered query, and the late-bound data source.
/// </summary>
[Trait("Category", "Local")]
public sealed class ClientConfigurationMappingTests(ClientConfigurationMappingTests.Fixture fixture)
    : IClassFixture<ClientConfigurationMappingTests.Fixture>
{
    public sealed class Fixture : PgDataSourceFixture
    {
        public override async ValueTask InitializeAsync()
        {
            await base.InitializeAsync();

            using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            using var command = new NpgsqlCommand("""
                CREATE TABLE IF NOT EXISTS client_settings (
                    client_id TEXT,
                    key       TEXT NOT NULL,
                    value     TEXT NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS client_settings_key
                    ON client_settings ((COALESCE(client_id, '')), key);

                INSERT INTO client_settings (client_id, key, value) VALUES
                    (NULL,   'theme',    'dark'),
                    (NULL,   'language', 'en'),
                    ('acme', 'theme',    'light'),
                    ('beta', 'language', 'de')
                ON CONFLICT DO NOTHING;

                -- A table with no client column at all: the client is spelled into the key,
                -- env-style, so the separator alias has to produce the overlay.
                CREATE TABLE IF NOT EXISTS env_style_settings (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                INSERT INTO env_style_settings (key, value) VALUES
                    ('theme',                      'dark'),
                    ('language',                   'en'),
                    ('sa__clients__acme__theme',   'light'),
                    ('sa__clients__beta__api_key', 'k-123'),
                    ('single_underscore_key',      'single_underscore_value')
                ON CONFLICT DO NOTHING;
                """, connection);

            await command.ExecuteNonQueryAsync();
        }
    }

    private const string AllClientsSql = "SELECT client_id, key, value FROM client_settings";

    [Fact]
    public void ClientColumn_MapsRowsToTheClientOverlay()
    {
        var builder = new ConfigurationBuilder();
        builder.AddSaPostgreSqlConfiguration(
            fixture.ConnectionString,
            new PostgreSqlConfigurationOptions { SelectSql = AllClientsSql, ClientColumn = "client_id" });

        IConfigurationRoot configuration = builder.Build();

        // Rows with a NULL client stay base values…
        Assert.Equal("dark", configuration["theme"]);
        Assert.Equal("en", configuration["language"]);

        // …and the rest land under sa:clients:{client}:{key}.
        Assert.Equal("light", configuration["sa:clients:acme:theme"]);
        Assert.Equal("de", configuration["sa:clients:beta:language"]);

        Assert.Equal(["acme", "beta"], configuration.GetClientNames().Order());
        Assert.Equal("light", configuration.ForClient("acme")["theme"]);
        Assert.Equal("en", configuration.ForClient("acme")["language"]);
        Assert.Equal("de", configuration.ForClient("beta")["language"]);
        Assert.Equal("dark", configuration.ForClient("beta")["theme"]);
    }

    [Fact]
    public void SeparatorAlias_BuildsTheClientOverlay_WithoutAClientColumn()
    {
        // No client column at all: the client is spelled into the key, env-style (`__`).
        // Normalization runs before the client prefix is applied, so the separator alias alone
        // has to produce sa:clients:{client}:{key}.
        var builder = new ConfigurationBuilder();
        builder.AddSaPostgreSqlConfiguration(
            fixture.ConnectionString,
            new PostgreSqlConfigurationOptions { SelectSql = "SELECT key, value FROM env_style_settings", DoubleUnderscoreAsSeparator = true, });

        IConfigurationRoot configuration = builder.Build();

        // Both spellings answer for the same setting…
        Assert.Equal("light", configuration["sa:clients:acme:theme"]);
        Assert.Equal("light", configuration["sa__clients__acme__theme"]);

        // …the clients are discovered from the normalized keys…
        Assert.Equal(["acme", "beta"], configuration.GetClientNames().Order());

        // …and the overlay falls back to the base layer where the client has no override.
        Assert.Equal("light", configuration.ForClient("acme")["theme"]);
        Assert.Equal("en", configuration.ForClient("acme")["language"]);
        Assert.Equal("k-123", configuration.ForClient("beta")["api_key"]);

        // A single underscore is data, not a separator: `api_key` survives intact.
        Assert.Equal("single_underscore_value", configuration["single_underscore_key"]);
    }

    [Fact]
    public void ConstantClient_MapsAFilteredQueryToThatClient()
    {
        var builder = new ConfigurationBuilder();
        builder.AddSaPostgreSqlConfiguration(
            fixture.ConnectionString,
            new PostgreSqlConfigurationOptions { SelectSql = "SELECT key, value FROM client_settings WHERE client_id = @client_id", Parameters = [new NpgsqlParameter("client_id", "acme")], Client = "acme" });

        IConfigurationRoot configuration = builder.Build();

        Assert.Equal("light", configuration["sa:clients:acme:theme"]);
        Assert.Equal("light", configuration.ForClient("acme")["theme"]);

        // A filtered source contributes only its own client: nothing leaks into the base layer.
        Assert.Null(configuration["theme"]);
        Assert.Equal(["acme"], configuration.GetClientNames());
    }

    [Fact]
    public async Task DynamicReload_SeesAClientAddedAfterStartup()
    {
        using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var builder = new ConfigurationBuilder();
        builder.AddSaPostgreSqlConfiguration(
            fixture.ConnectionString,
            new PostgreSqlConfigurationOptions { SelectSql = AllClientsSql, ClientColumn = "client_id" });

        IConfigurationRoot configuration = builder.Build();
        Assert.DoesNotContain("gamma", configuration.GetClientNames());

        using (var insert = new NpgsqlCommand(
            "INSERT INTO client_settings (client_id, key, value) VALUES ('gamma', 'theme', 'sea') ON CONFLICT DO NOTHING;",
            connection))
        {
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await SaConfigurationRuntime.GetOrAdd(builder)
            .ReloadDynamicAsync(TestContext.Current.CancellationToken);

        // One query returns every client, so a new one needs no restart and no second source.
        Assert.Contains("gamma", configuration.GetClientNames());
        Assert.Equal("sea", configuration.ForClient("gamma")["theme"]);
    }

    [Fact]
    public async Task FromServices_IsLateBound_AndResolvesAfterTheHostIsBuilt()
    {
        var builder = new ConfigurationBuilder();
        builder.AddSaConfiguration(_ => { });
        builder.AddSaPostgreSqlConfiguration(
            new PostgreSqlConfigurationOptions { SelectSql = "SELECT key, value FROM client_settings WHERE client_id IS NULL" });

        IConfigurationRoot configuration = builder.Build();

        // Registration time: no container yet, so the late-bound layer contributes nothing.
        Assert.Null(configuration["theme"]);

        var services = new ServiceCollection();
        services.AddSingleton(fixture.DataSource);          // the application's own pool
        services.AddSaConfigurationRuntime(builder);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ISaConfigurationReloader>()
            .EnsureInitializedAsync(TestContext.Current.CancellationToken);

        Assert.Equal("dark", configuration["theme"]);
        Assert.Equal("en", configuration["language"]);
    }

    [Fact]
    public void ClientColumn_MustBeTheFirstColumn()
    {
        var builder = new ConfigurationBuilder();
        builder.AddSaPostgreSqlConfiguration(
            fixture.ConnectionString,
            new PostgreSqlConfigurationOptions { SelectSql = "SELECT key, value, client_id FROM client_settings", ClientColumn = "client_id" });

        var exception = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("first column", exception.Message);
    }
}
