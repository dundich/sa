using Sa.Configuration;
using Sa.Configuration.PostgreSql;
using Sa.Data.PostgreSql;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Configuration.AddSaDefaultConfiguration();

const string PG_KEY = "sa:pg:connection";

string connectionString = builder.Configuration[PG_KEY]
    ?? throw new ArgumentException(PG_KEY);

using var ds = new PgDataSource(new(connectionString));

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


builder.Configuration.AddPostgreSqlConfiguration(new PostgreSqlConfigurationOptions
(
    ConnectionString: connectionString,
    SelectSql: "select * from settings"
));


builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}


var todosApi = app.MapGroup("/settings");

todosApi.MapGet("/", (IConfiguration configuration) => new Settings[] {
    new (Key: PG_KEY, Value: configuration[PG_KEY]),
    new (Key: "theme", Value: configuration["theme"]),
    new (Key: "language", Value: configuration["language"]),
    new (Key: "notifications", Value: configuration["notifications"]),
    new (Key: "secret", Value: configuration["secret"])
}).WithName("GetSettings");


app.Run();

public sealed record Settings(string Key, string? Value);

[JsonSerializable(typeof(Settings[]))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{

}
