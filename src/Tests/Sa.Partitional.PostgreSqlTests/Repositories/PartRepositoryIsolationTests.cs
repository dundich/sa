using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Configuration.Builder;
using Sa.Partitional.PostgreSql.Repositories;
using Sa.Partitional.PostgreSql.Settings;
using Sa.Partitional.PostgreSql.SqlBuilder;

namespace Sa.Partitional.PostgreSqlTests.Repositories;

/// <summary>
/// Unit tests (no database) for the per-table isolation of <see cref="PartRepository.MigrateAsync"/>:
/// one broken table is logged and skipped, every other table still gets its DDL, and the run as a
/// whole then fails with the original error.
/// </summary>
public class PartRepositoryIsolationTests
{
    private static readonly DateTimeOffset Day = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    private static PartRepository Create(SchemaBuilder schema, RecordingDataSource dataSource)
        => new(
            dataSource,
            new SqlBuilder(new TableSettingsStorage(schema.Build())),
            new NoopAdopter(),
            NullLogger<PartRepository>.Instance);

    private static SchemaBuilder TwoTables()
    {
        SchemaBuilder schema = new("public");

        schema.AddTable("broken", "id bigint NOT NULL", "tenant text NOT NULL", "created_at bigint NOT NULL")
            .PartByList("tenant")
            .PartByRange(PgPartBy.Day);

        schema.AddTable("healthy", "id bigint NOT NULL", "created_at bigint NOT NULL")
            .PartByRange(PgPartBy.Day);

        return schema;
    }

    [Fact]
    public async Task AFailedResolution_DoesNotStarveTheOtherTables_AndTheRunStillFails()
    {
        RecordingDataSource dataSource = new();
        PartRepository repository = Create(TwoTables(), dataSource);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.MigrateAsync(
                [Day],
                table => table == "public.broken"
                    ? throw new InvalidOperationException("resolution failed")
                    : Task.FromResult<StrOrNum[][]>([]),
                TestContext.Current.CancellationToken));

        Assert.Equal("resolution failed", error.Message);

        // The healthy table still ran its DDL.
        Assert.Contains(dataSource.Statements, sql => sql.Contains("\"public\".\"healthy\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedDdl_DoesNotStarveTheOtherTables_AndTheRunStillFails()
    {
        RecordingDataSource dataSource = new()
        {
            FailOn = sql => sql.Contains("\"public\".\"broken\"", StringComparison.Ordinal)
                ? new InvalidOperationException("ddl failed")
                : null,
        };

        PartRepository repository = Create(TwoTables(), dataSource);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.MigrateAsync(
                [Day],
                table => Task.FromResult<StrOrNum[][]>(table == "public.broken" ? [["acme"]] : []),
                TestContext.Current.CancellationToken));

        Assert.Equal("ddl failed", error.Message);

        Assert.Contains(dataSource.Statements, sql => sql.Contains("\"public\".\"healthy\"", StringComparison.Ordinal));
    }

    private sealed class NoopAdopter : IPartTableAdopter
    {
        public Task EnsureAdoptedAsync(string tableName, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Records the DDL it is given and can fail a chosen statement. Everything else is unsupported:
    /// the migration path only issues <see cref="ExecuteNonQueryAsync(string, CancellationToken)"/>.
    /// </summary>
    private sealed class RecordingDataSource : IPgDataSource
    {
        private readonly List<string> _statements = [];

        public IReadOnlyList<string> Statements
        {
            get
            {
                lock (_statements) return [.. _statements];
            }
        }

        /// <summary>Returns the exception a statement should fail with, or <c>null</c> to succeed.</summary>
        public Func<string, Exception?>? FailOn { get; init; }

        public Task<int> ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken = default)
        {
            lock (_statements) _statements.Add(sql);

            if (FailOn?.Invoke(sql) is { } failure) return Task.FromException<int>(failure);

            return Task.FromResult(0);
        }

        public string GetSearchPath() => string.Empty;

        public string GetConnectionString() => string.Empty;

        public ValueTask<NpgsqlConnection> OpenDbConnectionAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<PgTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> ExecuteNonQueryAsync(
            string sql, Action<NpgsqlCommand>? initCommand, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> ExecuteNonQueryAsync(
            string sql, IReadOnlyCollection<NpgsqlParameter> parameters, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<object?> ExecuteScalarAsync(
            string sql, Action<NpgsqlCommand>? initCommand, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<object?> ExecuteScalarAsync(string sql, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<T> ExecuteScalarAsync<T>(
            string sql, Action<NpgsqlCommand>? initCommand, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<T> ExecuteScalarTypedAsync<T>(
            string sql, Action<NpgsqlCommand>? initCommand = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<T> ExecuteScalarTypedAsync<T>(
            string sql, IReadOnlyCollection<NpgsqlParameter> parameters, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> ExecuteReaderAsync(
            string sql, Action<NpgsqlDataReader, int> read, Action<NpgsqlCommand>? initCommand,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> ExecuteReaderAsync(
            string sql, Action<NpgsqlDataReader, int> read, IReadOnlyCollection<NpgsqlParameter> parameters,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> ExecuteReaderAsync(
            string sql, Action<NpgsqlDataReader, int> read, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<List<T>> ExecuteReaderListAsync<T>(
            string sql, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<List<T>> ExecuteReaderListAsync<T>(
            string sql, Func<NpgsqlDataReader, T> read, IReadOnlyCollection<NpgsqlParameter> parameters,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<T> ExecuteReaderFirstAsync<T>(string sql, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<T> ExecuteReaderFirstAsync<T>(
            string sql, IReadOnlyCollection<NpgsqlParameter> parameters, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<ulong> BeginBinaryImportAsync(
            string sql, Func<NpgsqlBinaryImporter, CancellationToken, Task<ulong>> write,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
