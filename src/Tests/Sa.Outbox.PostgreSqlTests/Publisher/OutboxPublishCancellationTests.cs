using Npgsql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.Publication;
using Sa.Outbox.Publication;

namespace Sa.Outbox.PostgreSqlTests.Publisher;

/// <summary>
/// Cancelling a publish is the caller saying "do not store these messages". The outbox is the
/// record of what was asked for, so storing them anyway is not a benign outcome: the caller sees
/// the failure, retries or gives up, and the message is delivered regardless.
/// <para>
/// The contract is best-effort rather than transactional across chunks — one publish is split into
/// <c>MaxBatchSize</c> chunks, each its own transaction — so the tests pin both halves: nothing is
/// stored before the first write, and a cancellation that lands after an earlier chunk has already
/// committed keeps that chunk (at-least-once, and the caller must be idempotent).
/// </para>
/// </summary>
public class OutboxPublishCancellationTests(OutboxPublishCancellationTests.Fixture fixture)
    : IClassFixture<OutboxPublishCancellationTests.Fixture>
{
    public class Fixture : OutboxPostgreSqlFixture<IOutboxMessagePublisher>
    {
    }

    private IOutboxMessagePublisher Publisher => fixture.Sub;

    [Fact]
    public async Task Publish_CancelledToken_ThrowsAndStoresNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Publisher.Publish(
                [PublishCancelMessages.Msg("c1", 701), PublishCancelMessages.Msg("c2", 701)],
                701,
                cts.Token).AsTask());

        Assert.Equal(0, await PublishCancelMessages.Count(fixture.DataSource, 701));
    }

    [Fact]
    public async Task PublishSingle_CancelledToken_ThrowsAndStoresNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Publisher.PublishSingle(
                PublishCancelMessages.Msg("c3", 702), 702, cts.Token).AsTask());

        Assert.Equal(0, await PublishCancelMessages.Count(fixture.DataSource, 702));
    }

    /// <summary>
    /// The transactional form must honour the token the same way: the caller's transaction is the
    /// record, and a cancelled publish must leave it usable for a retry rather than half-written.
    /// </summary>
    [Fact]
    public async Task Publish_InCallerTransaction_CancelledToken_ThrowsAndWritesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        CancellationToken testToken = TestContext.Current.CancellationToken;

        await using NpgsqlConnection conn = await fixture.DataSource.OpenDbConnection(testToken);
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(testToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Publisher.Publish([PublishCancelMessages.Msg("c4", 703)], 703, tx, cts.Token).AsTask());

        // The write never started, so the transaction is not aborted and the caller can still use it.
        await tx.RollbackAsync(testToken);

        Assert.Equal(0, await PublishCancelMessages.Count(fixture.DataSource, 703));
    }
}

/// <summary>
/// The partial-publish half of the same question, with its own fixture because the cancellation has
/// to fire between two chunks: the payload-id delegate runs while a chunk is being filled, and a
/// chunk size of one turns "the second message" into "after the first commit".
/// </summary>
public class OutboxPublishCancellationBetweenChunksTests(
    OutboxPublishCancellationBetweenChunksTests.Fixture fixture)
    : IClassFixture<OutboxPublishCancellationBetweenChunksTests.Fixture>
{
    public class Fixture : OutboxPostgreSqlFixture<IOutboxMessagePublisher>
    {
        public CancellationTokenSource Cts { get; } = new();

        public Fixture() : base()
        {
            Services.AddSaOutbox(builder => builder
                .WithMetadata((_, metadata) => metadata.AddMetadata<TestMessage>("root_1", message =>
                {
                    // Runs once per message while its chunk is filled, i.e. after the previous
                    // chunk has already been committed.
                    if (message.PayloadId == "second")
                        Cts.Cancel();

                    return message.PayloadId;
                }))
                .WithPublishSettings((_, settings) => settings.WithMaxBatchSize(1)));
        }
    }

    [Fact]
    public async Task Publish_CancelledBetweenChunks_KeepsTheChunkThatAlreadyCommitted()
    {
        const int tenantId = 704;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Sub.Publish(
                [
                    PublishCancelMessages.Msg("first", tenantId),
                    PublishCancelMessages.Msg("second", tenantId),
                    PublishCancelMessages.Msg("third", tenantId),
                ],
                tenantId,
                fixture.Cts.Token).AsTask());

        // Exactly the chunk committed before the cancellation: the batch is not atomic across
        // chunks, and the returned count is lost with the exception.
        Assert.Equal(1, await PublishCancelMessages.Count(fixture.DataSource, tenantId));
    }
}

/// <summary>
/// Counting rows needs the message table to exist, and these tests assert on the *absence* of rows —
/// a fresh container has no migration yet, so a missing table is exactly zero.
/// </summary>
internal static class PublishCancelMessages
{
    private static readonly string Table =
        $"{PgOutboxTableSettings.Defaults.DatabaseTableName}{PgOutboxTableSettings.MessageTable.Suffix}";

    private static readonly string TenantColumn = new PgOutboxTableSettings().Message.Fields.TenantId;

    public static TestMessage Msg(string id, int tenantId) =>
        new() { PayloadId = id, Content = id, TenantId = tenantId };

    public static async Task<long> Count(Sa.Data.PostgreSql.IPgDataSource dataSource, int tenantId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        bool exists = await dataSource.ExecuteReaderFirst<bool>(
            $"select to_regclass('{Table}') is not null", ct);

        if (!exists) return 0;

        return await dataSource.ExecuteReaderFirst<long>(
            $"select count(*) from {Table} where {TenantColumn} = {tenantId}", ct);
    }
}
