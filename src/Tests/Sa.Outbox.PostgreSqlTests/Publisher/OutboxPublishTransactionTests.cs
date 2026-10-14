using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.Publication;

namespace Sa.Outbox.PostgreSqlTests.Publisher;

/// <summary>
/// Verifies that published messages can be enlisted in a caller-controlled PostgreSQL transaction
/// (an explicit <c>NpgsqlTransaction</c> or one opened via
/// <see cref="IPgDataSource.BeginTransaction"/>), and that the same-database guard rejects foreign
/// transactions.
/// </summary>
public class OutboxPublishTransactionTests(OutboxPublishTransactionTests.Fixture fixture)
    : IClassFixture<OutboxPublishTransactionTests.Fixture>
{
    public class Fixture : OutboxPostgreSqlFixture<IOutboxMessagePublisher>
    {
    }

    private IOutboxMessagePublisher Publisher => fixture.Sub;

    private IPgDataSource DataSource => fixture.DataSource;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Publish_ExplicitTransaction_Commit_MakesRowsVisible()
    {
        await using NpgsqlConnection conn = await DataSource.OpenDbConnection(Ct);
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(Ct);

        ulong result = await Publisher.Publish([Msg(101)], tenantId: 101, tx, Ct);

        Assert.Equal(1UL, result);

        await tx.CommitAsync(Ct);

        Assert.Equal(1, await Count(101));
    }

    [Fact]
    public async Task Publish_ExplicitTransaction_Rollback_LeavesNoRows()
    {
        await using NpgsqlConnection conn = await DataSource.OpenDbConnection(Ct);
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(Ct);

        await Publisher.Publish([Msg(102)], tenantId: 102, tx, Ct);

        await tx.RollbackAsync(Ct);

        Assert.Equal(0, await Count(102));
    }

    [Fact]
    public async Task Publish_DataSourceBeginTransaction_Commit_MakesRowsVisible()
    {
        await using PgTransaction sqlTx = await DataSource.BeginTransaction(Ct);

        await Publisher.Publish([Msg(108)], tenantId: 108, sqlTx.Transaction, Ct);

        await sqlTx.CommitAsync(Ct);

        Assert.Equal(1, await Count(108));
    }

    [Fact]
    public async Task Publish_DataSourceBeginTransaction_DisposeWithoutCommit_RollsBack()
    {
        await using (PgTransaction sqlTx = await DataSource.BeginTransaction(Ct))
        {
            await Publisher.Publish([Msg(109)], tenantId: 109, sqlTx.Transaction, Ct);
        }

        Assert.Equal(0, await Count(109));
    }

    [Fact]
    public async Task Publish_TransactionFromDifferentDatabase_Throws()
    {
        const string otherDb = "sa_outbox_other";

        await DataSource.ExecuteNonQuery($"CREATE DATABASE {otherDb}", Ct);

        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = otherDb,
        };

        await using NpgsqlConnection conn = await NpgsqlDataSource.Create(builder.ConnectionString).OpenConnectionAsync(Ct);
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(Ct);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Publisher.Publish([Msg(110)], tenantId: 110, tx, Ct).AsTask());

        Assert.Contains("different database", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TestMessage Msg(int tenantId) => new()
    {
        PayloadId = $"p-{tenantId}",
        Content = $"content-{tenantId}",
        TenantId = tenantId,
    };

    private async Task<int> Count(int tenantId)
    {
        string sql = $"""
            SELECT count(*) FROM
              {PgOutboxTableSettings.Defaults.DatabaseTableName}{PgOutboxTableSettings.MessageTable.Suffix}
            WHERE {OutboxFieldDefaults.TenantId} = @t
            """;

        return await DataSource.ExecuteReaderFirst<int>(
            sql,
            [new NpgsqlParameter("t", tenantId)],
            Ct);
    }
}