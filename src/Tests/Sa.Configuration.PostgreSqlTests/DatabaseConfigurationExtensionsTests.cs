using Microsoft.Extensions.Configuration;
using Npgsql;
using Sa.Configuration.PostgreSql;
using Sa.Data.PostgreSql.Fixture;

namespace Sa.Configuration.PostgreSqlTests;


public sealed class DatabaseConfigurationExtensionsTests(DatabaseConfigurationExtensionsTests.Fixture fixture)
    : IClassFixture<DatabaseConfigurationExtensionsTests.Fixture>
{

    public sealed class Fixture : PgDataSourceFixture
    {
        public override async ValueTask InitializeAsync()
        {
            await base.InitializeAsync();

            using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            // Создание таблицы
            using (var createTableCommand = new NpgsqlCommand(@"
                CREATE TABLE IF NOT EXISTS configuration (
                    key TEXT PRIMARY KEY,
                    value TEXT
                );", connection))
            {
                await createTableCommand.ExecuteNonQueryAsync();
            }

            // Вставка данных
            using var insertCommand = new NpgsqlCommand(@"
                INSERT INTO configuration (key, value) VALUES
                ('Setting1', 'Value1'),
                ('Setting2', 'Value2'),
                ('Setting3', 'Value3'),
                ('Setting4', 'Value4'),
                ('Setting5', 'Value5'),
                ('SettingNull', null)
                ON CONFLICT (key) DO NOTHING;", connection);
            await insertCommand.ExecuteNonQueryAsync();
        }
    }


    [Fact]
    public void AddPostgreSqlConfiguration_ShouldAddDatabaseConfigurationSource()
    {
        // Arrange
        var builder = new ConfigurationBuilder();

        var options = new PostgreSqlConfigurationOptions(
            fixture.ConnectionString,
            "SELECT key, value FROM configuration");

        // Act
        builder.AddSaPostgreSqlConfiguration(options);
        var configuration = builder.Build();

        // Assert
        Assert.NotNull(configuration);

        Assert.Equal("Value3", configuration["Setting3"]);
        Assert.Null(configuration["SettingNull"]);
    }
}
