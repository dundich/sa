using Npgsql;
using Sa.Configuration;
using Sa.Configuration.Clients;
using Sa.Configuration.PostgreSql;
using Sa.Data.PostgreSql;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);

// Static layer: appsettings.json / appsettings.{env}.json / environment variables / command-line
// arguments are registered by the host already. Sa adds the secret pass ({{placeholder}} + key
// aliases).
builder.Configuration.AddSaConfiguration(sa => sa.UseSecrets());

const string PG_KEY = "sa:pg:connection";

string connectionString = builder.Configuration[PG_KEY]
    ?? throw new ArgumentException(PG_KEY);

using var ds = IPgDataSource.Create(connectionString);

// Two tables: `setting` holds the shared core values, `client_settings` holds the clients'
// overrides split by client_id. The key names are the same in both tables.
ds.ExecuteScalarAsync("""
    CREATE TABLE IF NOT EXISTS setting (
        key   TEXT PRIMARY KEY,
        value TEXT NOT NULL
    );

    INSERT INTO setting (key, value) VALUES
        ('s3_url',        'A'),
        ('theme',         'dark'),
        ('language',      'en'),
        ('notifications', 'enabled')
    ON CONFLICT DO NOTHING;

    CREATE TABLE IF NOT EXISTS client_settings (
        client_id TEXT NOT NULL,
        key       TEXT NOT NULL,
        value     TEXT NOT NULL,
        PRIMARY KEY (client_id, key)
    );

    INSERT INTO client_settings (client_id, key, value) VALUES
        ('acme', 's3_url',        'B1'),
        ('acme', 'theme',         'light'),
        ('acme', 'notifications', 'disabled'),
        ('beta', 's3_url',        'C'),
        ('beta', 'language',      'de')
    ON CONFLICT DO NOTHING;
""", null).Wait();

// Dynamic layer: the core values come from `setting`. The explicit data source keeps a single
// connection pool for the whole application and needs no late-bound resolution.
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.DataSource(ds),
    new PostgreSqlConfigurationOptions
    {
        SelectSql = "SELECT key, value FROM setting",
    });

// Client layer: one query returns every client's rows, the registry groups them by client_id and
// builds a root per client. Keys are stored verbatim — no prefix.
builder.Services.AddSaClientConfigurations(builder.Configuration, clients =>
{
    clients.Add<IPgDataSource>(static (dataSource, cancellationToken) =>
        ReadClientSettingsAsync(dataSource, cancellationToken));
});

builder.Services.AddSingleton(ds);

// A known client can be injected by key: the service constructor receives acme's own root.
builder.Services.AddSingleton<AcmeStorage>();

// Runtime: reload on demand (no background timer unless DynamicReload is switched on). One
// ReloadDynamicAsync refreshes the core and the client roots.
builder.Services.AddSaConfigurationRuntime(builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

var app = builder.Build();

var settingsApi = app.MapGroup("/settings");

// A connection string carries the password, so never hand one to a client as-is.
// Mask it for display; everything else on this endpoint is shown verbatim.
static string MaskPassword(string? connectionString) => connectionString is null
    ? string.Empty
    : new NpgsqlConnectionStringBuilder(connectionString) { Password = "***" }.ConnectionString;

// Base (core) values and the discovered clients.
settingsApi.MapGet("/", (IConfiguration configuration, IClientConfigurations clients) => new Settings[] {
    new (Key: PG_KEY, Value: MaskPassword(configuration[PG_KEY])),
    new (Key: "s3_url", Value: configuration["s3_url"]),
    new (Key: "theme", Value: configuration["theme"]),
    new (Key: "language", Value: configuration["language"]),
    new (Key: "notifications", Value: configuration["notifications"]),
    new (Key: "secret", Value: configuration["secret"]),
    new (Key: "clients", Value: string.Join(", ", clients.Clients))
}).WithName("GetSettings");

// One client: its own root laid over the core values. An unknown client reads the core only.
settingsApi.MapGet("/{client}", (string client, IClientConfigurations clients) =>
{
    IConfiguration clientConfiguration = clients.GetClient(client);

    return new Settings[] {
        new (Key: "s3_url", Value: clientConfiguration["s3_url"]),
        new (Key: "theme", Value: clientConfiguration["theme"]),
        new (Key: "language", Value: clientConfiguration["language"]),
        new (Key: "notifications", Value: clientConfiguration["notifications"]),
        new (Key: "secret", Value: clientConfiguration["secret"]),
    };
}).WithName("GetClientSettings");

// Keyed injection for a known client: the client's root arrives through the constructor, so the
// handler never touches the registry. `/settings/acme` is a literal route, so it wins over
// `/settings/{client}`.
settingsApi.MapGet("/acme", (AcmeStorage acme) => new Settings[] {
    new (Key: "s3_url", Value: acme.S3Url),
    new (Key: "theme", Value: acme.Theme),
    new (Key: "language", Value: acme.Language),
    new (Key: "notifications", Value: acme.Notifications),
}).WithName("GetAcmeSettingsFromKeyedService");

// Manual refresh of the dynamic layer only: files on disk are not re-read. The client providers
// are re-read in the same call.
settingsApi.MapPost("/reload", async (ISaConfigurationReloader reloader) =>
{
    await reloader.ReloadDynamicAsync();
    return Results.Ok();
}).WithName("ReloadDynamicConfiguration");

app.Run();

static async ValueTask<IReadOnlyList<ClientSetting>> ReadClientSettingsAsync(
    IPgDataSource dataSource,
    CancellationToken cancellationToken)
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

public sealed record Settings(string Key, string? Value);

/// <summary>Client ids used by the sample.</summary>
public static class DemoClients
{
    /// <summary>The known client resolved by key.</summary>
    public const string Acme = "acme";
}

/// <summary>
/// A known client resolved from the container by key: the constructor receives acme's own root,
/// already laid over the core values.
/// </summary>
public sealed class AcmeStorage([FromKeyedServices(DemoClients.Acme)] IConfiguration configuration)
{
    public string? S3Url => configuration["s3_url"];

    public string? Theme => configuration["theme"];

    public string? Language => configuration["language"];

    public string? Notifications => configuration["notifications"];
}

[JsonSerializable(typeof(Settings[]))]
internal sealed partial class AppJsonSerializerContext : JsonSerializerContext
{
}
