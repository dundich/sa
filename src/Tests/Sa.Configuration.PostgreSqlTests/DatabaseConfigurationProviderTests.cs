using Microsoft.Extensions.Configuration;
using Npgsql;
using Sa.Configuration.PostgreSql;
using Sa.Data.PostgreSql.Fixture;

namespace Sa.Configuration.PostgreSqlTests;


[Trait("Category", "Local")]
public sealed class DatabaseConfigurationProviderTests(DatabaseConfigurationProviderTests.Fixture fixture)
    : IClassFixture<DatabaseConfigurationProviderTests.Fixture>
{
    public sealed class Fixture : PgDataSourceFixture
    {
        public override async ValueTask InitializeAsync()
        {
            await base.InitializeAsync();

            using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            // Основная таблица
            using (var createTableCommand = new NpgsqlCommand(@"
                CREATE TABLE IF NOT EXISTS config_provider_test (
                    key TEXT PRIMARY KEY,
                    value TEXT
                );", connection))
            {
                await createTableCommand.ExecuteNonQueryAsync();
            }

            // Вставка данных
            using var insertCommand = new NpgsqlCommand(@"
                INSERT INTO config_provider_test (key, value) VALUES
                ('NormalKey', 'normal_value'),
                ('TrimmedKey ', ' trimmed_value '),
                ('WhitespaceKey', '   spaced   '),
                ('EmptyValueKey', ''),
                ('NullValueKey', null),
                ('  LeadingSpaceKey', 'leading_space_value'),
                ('sa__pg__host', 'db-host'),
                ('sa:pg:port', '5432'),
                ('single_underscore_key', 'single_underscore_value'),
                ('literal__double', 'literal_double_value')
                ON CONFLICT (key) DO NOTHING;", connection);

            await insertCommand.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void Load_HandlesEmptyResult_WhenNoRowsMatch()
    {
        // Arrange
        var builder = new ConfigurationBuilder();
        var options = new PostgreSqlConfigurationOptions(
            fixture.ConnectionString,
            "SELECT key, value FROM config_provider_test WHERE key = 'nonexistent_row'");

        builder.AddSaPostgreSqlConfiguration(options);

        // Act
        var configuration = builder.Build();

        // Assert
        Assert.Null(configuration["nonexistent_row"]);
    }

    [Fact]
    public void Load_TrimsKeyAndValue_WhenTheyHaveWhitespace()
    {
        // Arrange
        var builder = new ConfigurationBuilder();
        var options = new PostgreSqlConfigurationOptions(
            fixture.ConnectionString,
            "SELECT key, value FROM config_provider_test");

        builder.AddSaPostgreSqlConfiguration(options);

        // Act
        var configuration = builder.Build();

        // Assert
        Assert.Equal("trimmed_value", configuration["TrimmedKey"]);
        Assert.Equal("spaced", configuration["WhitespaceKey"]);
        Assert.Equal("leading_space_value", configuration["LeadingSpaceKey"]);
    }

    [Fact]
    public void Load_HandlesEmptyStringValue()
    {
        // Arrange
        var builder = new ConfigurationBuilder();
        var options = new PostgreSqlConfigurationOptions(
            fixture.ConnectionString,
            "SELECT key, value FROM config_provider_test WHERE key = 'EmptyValueKey'");

        builder.AddSaPostgreSqlConfiguration(options);

        // Act
        var configuration = builder.Build();

        // Assert
        Assert.Null(configuration["EmptyValueKey"]);
    }

    [Fact]
    public void Load_HandlesNullValue_ResultSetIsNull()
    {
        // Arrange
        var builder = new ConfigurationBuilder();
        var options = new PostgreSqlConfigurationOptions(
            fixture.ConnectionString,
            "SELECT key, value FROM config_provider_test WHERE key = 'NullValueKey'");

        builder.AddSaPostgreSqlConfiguration(options);

        // Act
        var configuration = builder.Build();

        // Assert
        Assert.Null(configuration["NullValueKey"]);
    }

    [Fact]
    public void Load_TreatsDoubleUnderscoreAsSeparator()
    {
        // A key stored in the database as sa__pg__host must be reachable as sa:pg:host,
        // the same convention AddEnvironmentVariables() applies to environment variables.
        // The alias is opt-in, so it must be switched on explicitly.
        var builder = new ConfigurationBuilder();
        var options = new PostgreSqlConfigurationOptions(
            fixture.ConnectionString,
            "SELECT key, value FROM config_provider_test WHERE key LIKE 'sa%' OR key = 'single_underscore_key'")
        with { DoubleUnderscoreAsSeparator = true };

        builder.AddSaPostgreSqlConfiguration(options);

        // Act
        var configuration = builder.Build();

        // Assert — both spellings in the database land on the same canonical `:` key.
        Assert.Equal("db-host", configuration["sa:pg:host"]);
        Assert.Equal("5432", configuration["sa:pg:port"]);

        // Reading through the alias spelling works as well, so the two are interchangeable.
        Assert.Equal("db-host", configuration["sa__pg__host"]);
        Assert.Equal("5432", configuration["sa__pg__port"]);

        // Single underscores are data, not separators.
        Assert.Equal("single_underscore_value", configuration["single_underscore_key"]);

        // The path is a real hierarchy, so section binding sees the children.
        Assert.Equal("db-host", configuration.GetSection("sa:pg")["host"]);
    }

    [Fact]
    public void Load_KeepsDoubleUnderscore_WhenSeparatorAliasIsDisabled()
    {
        // With the alias off, a key that legitimately contains __ is stored verbatim.
        var builder = new ConfigurationBuilder();
        var options = new PostgreSqlConfigurationOptions(
            fixture.ConnectionString,
            "SELECT key, value FROM config_provider_test WHERE key IN ('literal__double', 'sa__pg__host')")
        with { DoubleUnderscoreAsSeparator = false };

        builder.AddSaPostgreSqlConfiguration(options);

        // Act
        var configuration = builder.Build();

        // Assert
        Assert.Equal("literal_double_value", configuration["literal__double"]);
        Assert.Null(configuration["literal:double"]);
        Assert.Equal("db-host", configuration["sa__pg__host"]);
        Assert.Null(configuration["sa:pg:host"]);
    }

    [Fact]
    public async Task Load_Reload_DropsValuesRemovedFromDatabase()
    {
        // Arrange: seed a row that will be removed before the second load
        using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        using var seed = new NpgsqlCommand(
            "INSERT INTO config_provider_test (key, value) VALUES ('stale_key', 'stale_value') ON CONFLICT (key) DO NOTHING;",
            connection);
        await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        var builder = new ConfigurationBuilder();
        var options = new PostgreSqlConfigurationOptions(
            fixture.ConnectionString,
            "SELECT key, value FROM config_provider_test");

        builder.AddSaPostgreSqlConfiguration(options);

        var configuration = builder.Build();

        // Assert: the first load sees the row
        Assert.Equal("stale_value", configuration["stale_key"]);

        // Act: remove the row from the database, then reload
        using var del = new NpgsqlCommand("DELETE FROM config_provider_test WHERE key = 'stale_key';", connection);
        await del.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        configuration.Reload();

        // Assert: the stale row no longer lingers, surviving rows are intact
        Assert.Null(configuration["stale_key"]);
        Assert.Equal("normal_value", configuration["NormalKey"]);
    }
}
