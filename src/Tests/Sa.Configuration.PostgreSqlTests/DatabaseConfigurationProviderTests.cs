using Microsoft.Extensions.Configuration;
using Npgsql;
using Sa.Configuration.PostgreSql;
using Sa.Data.PostgreSql.Fixture;

namespace Sa.Configuration.PostgreSqlTests;


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
                ('  LeadingSpaceKey', 'leading_space_value')
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
}
