using Npgsql;
using Sa.Configuration;
using Sa.Configuration.Clients;
using Sa.Configuration.PostgreSql;
using Sa.Data.PostgreSql;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);

// Static layer: appsettings.json / appsettings.{env}.json / environment variables / command-line
// arguments are registered by the host already. Sa adds the secret pass ({{placeholder}} + key
// aliases) and the per-client overlay.
builder.Configuration.AddSaConfiguration(sa =>
{
    sa.UseSecrets();
    sa.UseClients();
});

const string PG_KEY = "sa:pg:connection";

string connectionString = builder.Configuration[PG_KEY]
    ?? throw new ArgumentException(PG_KEY);

using var ds = IPgDataSource.Create(connectionString);

// One table for everything: client_id NULL holds the base values, a non-null client_id holds that
// client's overrides. One query returns all of it, so a client added to the table is visible after a
// reload without a restart.
ds.ExecuteScalar("""
    CREATE TABLE IF NOT EXISTS settings (
        client_id TEXT,
        key       TEXT NOT NULL,
        value     TEXT NOT NULL
    );

    CREATE UNIQUE INDEX IF NOT EXISTS settings_client_key
        ON settings ((COALESCE(client_id, '')), key);

    INSERT INTO settings (client_id, key, value) VALUES
        (NULL,   'theme',         'dark'),
        (NULL,   'language',      'en'),
        (NULL,   'notifications', 'enabled'),
        ('acme', 'theme',         'light'),
        ('acme', 'notifications', 'disabled'),
        ('beta', 'language',      'de')
    ON CONFLICT DO NOTHING;
""", null).Wait();

// Dynamic layer: the explicit data source keeps a single connection pool for the whole application
// and needs no late-bound resolution, so the values are available immediately.
builder.Configuration.AddSaPostgreSqlConfiguration(
    PgConfigurationConnection.DataSource(ds),
    new PostgreSqlConfigurationOptions(
        // The provider takes the columns in this order: client, key, value.
        SelectSql: "SELECT client_id, key, value FROM settings")
    {
        ClientColumn = "client_id",
        ClientsSection = ClientConfiguration.DefaultSectionPath,
    });

// Runtime: reload on demand (no background timer unless DynamicReload is switched on) and a keyed
// IConfiguration per client.
builder.Services.AddSaConfigurationRuntime(builder.Configuration, runtime =>
{
    runtime.KeyedClientConfigurations = true;
});

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

// Base values and the list of clients declared by the overrides.
settingsApi.MapGet("/", (IConfiguration configuration) => new Settings[] {
    new (Key: PG_KEY, Value: MaskPassword(configuration[PG_KEY])),
    new (Key: "theme", Value: configuration["theme"]),
    new (Key: "language", Value: configuration["language"]),
    new (Key: "notifications", Value: configuration["notifications"]),
    new (Key: "secret", Value: configuration["secret"]),
    new (Key: "clients", Value: string.Join(", ", configuration.GetClientNames()))
}).WithName("GetSettings");

// One client: its overrides laid over the base values. An unknown client simply reads the base.
settingsApi.MapGet("/{client}", (string client, IConfiguration configuration) =>
{
    IConfiguration clientConfiguration = configuration.ForClient(client);

    return new Settings[] {
        new (Key: "theme", Value: clientConfiguration["theme"]),
        new (Key: "language", Value: clientConfiguration["language"]),
        new (Key: "notifications", Value: clientConfiguration["notifications"]),
        new (Key: "secret", Value: clientConfiguration["secret"]),
    };
}).WithName("GetClientSettings");

// Manual refresh of the dynamic layer only: files on disk are not re-read.
settingsApi.MapPost("/reload", async (ISaConfigurationReloader reloader) =>
{
    await reloader.ReloadDynamicAsync();
    return Results.Ok();
}).WithName("ReloadDynamicConfiguration");

app.Run();

#pragma warning disable S3903
public sealed record Settings(string Key, string? Value);


[JsonSerializable(typeof(Settings[]))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{

}
#pragma warning restore S3903
