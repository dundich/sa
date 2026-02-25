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


            // Создание таблицы
            using (var createTableExCommand = new NpgsqlCommand(@"
                CREATE TABLE IF NOT EXISTS configuration_ex (
                    key TEXT PRIMARY KEY,
                    value TEXT,
                    client_id INT
                );", connection))
            {
                await createTableExCommand.ExecuteNonQueryAsync();
            }


            using var insertExCommand = new NpgsqlCommand(@"
                INSERT INTO configuration_ex (key, value, client_id) VALUES
                ('Setting2', 'ValueEx2', 1)
                ON CONFLICT (key) DO NOTHING;", connection);

            await insertExCommand.ExecuteNonQueryAsync();
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

        var optionsEx = new PostgreSqlConfigurationOptions(
                fixture.ConnectionString,
                "SELECT key, value FROM configuration_ex where client_id = @client_id",
                [new("client_id", 1)]);

        builder.AddSaPostgreSqlConfiguration(optionsEx);


        var configuration = builder.Build();

        // Assert
        Assert.NotNull(configuration);

        Assert.Equal("Value3", configuration["Setting3"]);
        Assert.Null(configuration["SettingNull"]);

        Assert.Equal("ValueEx2", configuration["Setting2"]);
    }
}
