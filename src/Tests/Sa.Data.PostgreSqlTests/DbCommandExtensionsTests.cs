using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Data.PostgreSql.Fixture;

namespace Sa.Data.PostgreSqlTests;

/// <summary>
/// Pins the parameter shapes built by <see cref="DbCommandExtensions.AddParam{TProvider,T}"/>:
/// value types must arrive as typed <see cref="NpgsqlParameter{T}"/> (no boxing), while a null
/// reference keeps the untyped DBNull parameter.
/// </summary>
[Collection(nameof(PgDataSourceFixture))]
public class DbCommandExtensionsTests(PgDataSourceFixture fixture) : IClassFixture<PgDataSourceFixture>
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private sealed class BatchParams : INamePrefixProvider
    {
        public static string[] GetPrefixes() => ["v"];

        public static int MaxIndex => 4;
    }

    [Fact]
    public async Task AddParam_UsesTypedParameters_AndNullStaysUntyped()
    {
        await using var connection = await fixture.DataSource.OpenDbConnection(TestToken);
        await using var command = new NpgsqlCommand(
            "SELECT @v0::bigint, @v1::int, @v2::text, @v3::text", connection);

        command
            .AddParam<BatchParams, long>("v", 42L, 0)
            .AddParam<BatchParams, int>("v", 7, 1)
            .AddParam<BatchParams, string>("v", "hello", 2)
            .AddParam<BatchParams, string>("v", null, 3);

        // Value types no longer go through (object)value.
        Assert.IsType<NpgsqlParameter<long>>(command.Parameters[0]);
        Assert.IsType<NpgsqlParameter<int>>(command.Parameters[1]);
        Assert.IsType<NpgsqlParameter<string>>(command.Parameters[2]);

        // A null has no type to infer, so it stays the untyped DBNull parameter.
        Assert.Equal(typeof(NpgsqlParameter), command.Parameters[3].GetType());
        Assert.Same(DBNull.Value, command.Parameters[3].Value);

        await using var reader = await command.ExecuteReaderAsync(TestToken);

        Assert.True(await reader.ReadAsync(TestToken));
        Assert.Equal(42L, reader.GetInt64(0));
        Assert.Equal(7, reader.GetInt32(1));
        Assert.Equal("hello", reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
    }
}
