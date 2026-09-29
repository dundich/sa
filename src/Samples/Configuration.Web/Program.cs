using Npgsql;
using Sa.Configuration;
using Sa.Configuration.PostgreSql;
using Sa.Data.PostgreSql;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Configuration.AddSaConfiguration();

const string PG_KEY = "sa:pg:connection";

string connectionString = builder.Configuration[PG_KEY]
    ?? throw new ArgumentException(PG_KEY);

using var ds = IPgDataSource.Create(connectionString);

ds.ExecuteScalar("""
    CREATE TABLE IF NOT EXISTS settings (
        key TEXT PRIMARY KEY,
        value TEXT NOT NULL
    );

    INSERT INTO settings (key, value)
    VALUES
        ('theme', 'dark'),
        ('language', 'en'),
        ('notifications', 'enabled')
    ON CONFLICT (key) DO NOTHING;
""", null).Wait();


// The application already holds a data source, so hand it over instead of letting the
// configuration source open a second pool. Ownership stays here — `ds` is never disposed
// by the provider, and it carries the connection, so the connection string is ignored.
builder.Configuration.AddSaPostgreSqlConfiguration(
    new PostgreSqlConfigurationOptions(
        ConnectionString: string.Empty,
        // Name the columns: the provider takes the first two as (key, value), so the column
        // order is part of the contract and `select *` would make it an implicit one.
        SelectSql: "select key, value from settings"),
    ds);


builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});


var app = builder.Build();


var todosApi = app.MapGroup("/settings");

// A connection string carries the password, so never hand one to a client as-is.
// Mask it for display; everything else on this endpoint is shown verbatim.
static string MaskPassword(string? connectionString) => connectionString is null
    ? string.Empty
    : new NpgsqlConnectionStringBuilder(connectionString) { Password = "***" }.ConnectionString;


todosApi.MapGet("/", (IConfiguration configuration) => new Settings[] {
    new (Key: PG_KEY, Value: MaskPassword(configuration[PG_KEY])),
    new (Key: "theme", Value: configuration["theme"]),
    new (Key: "language", Value: configuration["language"]),
    new (Key: "notifications", Value: configuration["notifications"]),
    new (Key: "secret", Value: configuration["secret"])
}).WithName("GetSettings");


app.Run();

#pragma warning disable S3903
public sealed record Settings(string Key, string? Value);


[JsonSerializable(typeof(Settings[]))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{

}
#pragma warning restore S3903
