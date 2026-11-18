using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sa.Configuration;
using Sa.Configuration.Clients;
using Sa.Configuration.PostgreSql;
using Sa.Configuration.SecretStore;
using Sa.Data.PostgreSql;
using Sa.Data.PostgreSql.Fixture;

namespace Sa.Configuration.PostgreSqlTests;

/// <summary>
/// The client strategy against PostgreSQL: the shared values live in <c>setting</c>, every client's
/// overrides live in <c>client_settings</c> filtered by <c>client_id</c>, and the core configuration
/// stays untouched by the client roots.
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

            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand("""
                CREATE TABLE IF NOT EXISTS setting (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                INSERT INTO setting (key, value) VALUES
                    ('s3_url',   'A'),
                    ('theme',    'dark'),
                    ('language', 'en'),
                    ('auth_base_url', 'https://core.example.com')
                ON CONFLICT DO NOTHING;

                CREATE TABLE IF NOT EXISTS client_settings (
                    client_id TEXT NOT NULL,
                    key       TEXT NOT NULL,
                    value     TEXT NOT NULL,
                    PRIMARY KEY (client_id, key)
                );

                INSERT INTO client_settings (client_id, key, value) VALUES
                    ('client1', 's3_url', 'B1'),
                    ('client1', 'theme',  'light'),
                    ('client2', 's3_url', 'C'),
                    ('client2', 'language', 'de'),
                    ('client3', 's3_url', '{{zz_client_pw}}'),
                    ('client3', 'auth_base_url', 'https://client3.example.com')
                ON CONFLICT DO NOTHING;
                """, connection);

            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// The provider: one query returns rows for every client, exactly the flat shape the registry
    /// groups by <c>client_id</c>.
    /// </summary>
    private sealed class PgClientSettingsReader(IPgDataSource dataSource)
    {
        public async ValueTask<IReadOnlyList<ClientSetting>> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            List<ClientSetting> rows = [];

            await dataSource.ExecuteReaderAsync(
                "SELECT client_id, key, value FROM client_settings",
                (reader, _) => rows.Add(new ClientSetting(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2))),
                initCommand: null,
                cancellationToken);

            return rows;
        }
    }

    private const string CoreSql = "SELECT key, value FROM setting";

    private IConfiguration Core()
    {
        var builder = new ConfigurationBuilder();
        builder.AddSaPostgreSqlConfiguration(
            fixture.ConnectionString,
            new PostgreSqlConfigurationOptions { SelectSql = CoreSql });

        return builder.Build();
    }

    private ServiceProvider Clients(IConfiguration core)
    {
        var services = new ServiceCollection();
        services.AddSingleton(fixture.DataSource);
        services.AddSingleton<PgClientSettingsReader>();

        services.AddSaClientConfigurations(core, clients =>
            clients.Add<PgClientSettingsReader>((reader, ct) => reader.LoadAsync(ct)));

        return services.BuildServiceProvider();
    }

    [Fact]
    public void ClientRows_GoThroughTheSamePassAsTheCore()
    {
        var builder = new ConfigurationBuilder();
        builder.AddSaPostgreSqlConfiguration(
            fixture.ConnectionString,
            new PostgreSqlConfigurationOptions { SelectSql = CoreSql });

        builder.AddSaConfiguration(new SecretOptions
        {
            Args = [],
            LoadDotEnv = false,
            Encryption = new SecretEncryptionOptions { Enabled = false },
            Resolution = new SecretResolutionOptions
            {
                Resolver = key => key == "zz_client_pw" ? "s3cr3t" : null,
            },
            KeyAliases = new KeyAliasOptions
            {
                KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
            },
        });

        IConfigurationRoot core = builder.Build();

        var services = new ServiceCollection();
        services.AddSingleton(fixture.DataSource);
        services.AddSingleton<PgClientSettingsReader>();
        services.AddSaConfigurationRuntime(builder);
        services.AddSaClientConfigurations(core, clients =>
            clients.Add<PgClientSettingsReader>((reader, ct) => reader.LoadAsync(ct)));

        using ServiceProvider provider = services.BuildServiceProvider();

        // The reloader attaches the registry — and the transform chain — to the core's pass.
        _ = provider.GetRequiredService<ISaConfigurationReloader>();
        IClientConfigurations clients = provider.GetRequiredService<IClientConfigurations>();

        IConfiguration client3 = clients.GetClient("client3");

        // The secret stored in the table is substituted by the same chain the core uses…
        Assert.Equal("s3cr3t", client3["s3_url"]);

        // …and the flat alias key in the table fills the canonical path for this client only.
        Assert.Equal("https://client3.example.com", client3["sa:scope:api:auth:base_url"]);
        Assert.Equal("https://core.example.com", core["sa:scope:api:auth:base_url"]);

        // The core table is untouched.
        Assert.Equal("A", core["s3_url"]);
    }

    [Fact]
    public void CoreAndClients_ComeFromSeparateTables()
    {
        IConfiguration core = Core();

        using ServiceProvider provider = Clients(core);
        IClientConfigurations clients = provider.GetRequiredService<IClientConfigurations>();

        // The core table supplies the shared values.
        Assert.Equal("A", core["s3_url"]);
        Assert.Equal("dark", core["theme"]);
        Assert.Equal("en", core["language"]);

        // Every client has its own root with the same key names.
        Assert.Equal("B1", clients.GetClient("client1")["s3_url"]);
        Assert.Equal("C", clients.GetClient("client2")["s3_url"]);

        // The client value differs from the core value, and the core is not mutated.
        Assert.NotEqual(core["s3_url"], clients.GetClient("client1")["s3_url"]);
        Assert.Equal("A", core["s3_url"]);

        // A key the client does not override falls back to the core value.
        Assert.Equal("light", clients.GetClient("client1")["theme"]);
        Assert.Equal("en", clients.GetClient("client1")["language"]);
        Assert.Equal("de", clients.GetClient("client2")["language"]);
        Assert.Equal("dark", clients.GetClient("client2")["theme"]);
    }

    [Fact]
    public void Clients_AreDiscoveredFromTheRows()
    {
        IConfiguration core = Core();

        using ServiceProvider provider = Clients(core);
        IClientConfigurations clients = provider.GetRequiredService<IClientConfigurations>();

        Assert.Contains("client1", clients.Clients);
        Assert.Contains("client2", clients.Clients);
        Assert.True(clients.TryGet("client1", out _));
        Assert.False(clients.TryGet("unknown", out _));
        Assert.Equal("A", clients.GetClient("unknown")["s3_url"]);
    }

    [Fact]
    public void KeyedConfiguration_ResolvesAClientById()
    {
        IConfiguration core = Core();

        using ServiceProvider provider = Clients(core);

        Assert.Equal("B1", provider.GetRequiredKeyedService<IConfiguration>("client1")["s3_url"]);
        Assert.Equal("C", provider.GetRequiredKeyedService<IConfiguration>("client2")["s3_url"]);
        Assert.Equal("A", provider.GetRequiredKeyedService<IConfiguration>("unknown")["s3_url"]);
    }

    [Fact]
    public async Task DynamicReload_SeesAClientAddedAfterStartup()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        IConfiguration core = Core();

        using ServiceProvider provider = Clients(core);
        IClientConfigurations clients = provider.GetRequiredService<IClientConfigurations>();

        Assert.DoesNotContain("gamma", clients.Clients);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO client_settings (client_id, key, value) VALUES ('gamma', 's3_url', 'D') ON CONFLICT DO NOTHING;",
            connection))
        {
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            await clients.ReloadAsync(TestContext.Current.CancellationToken);

            Assert.Contains("gamma", clients.Clients);
            Assert.Equal("D", clients.GetClient("gamma")["s3_url"]);
            Assert.Equal("en", clients.GetClient("gamma")["language"]);
        }
        finally
        {
            // The fixture database is shared by the class, so the extra client must not leak into
            // the other tests.
            await using var delete = new NpgsqlCommand(
                "DELETE FROM client_settings WHERE client_id = 'gamma';",
                connection);

            await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void MissingRow_DelegatesToTheCoreValue()
    {
        IConfiguration core = Core();

        using ServiceProvider provider = Clients(core);
        IClientConfigurations clients = provider.GetRequiredService<IClientConfigurations>();

        // client2 has no theme row; the core value is used rather than an empty string.
        Assert.Equal("dark", clients.GetClient("client2")["theme"]);
    }
}
