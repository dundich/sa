using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Commands;

namespace Sa.Outbox.PostgreSqlTests.Commands;

/// <summary>
/// Covers <see cref="ErrorDeliveryCommand.GroupByException"/> — the split of a delivery batch into
/// the distinct <c>__error$</c> rows to insert and the per-exception lookup used to fill in
/// <c>task.error_id</c>.
/// </summary>
/// <remarks>
/// The primary key of an <c>__error$</c> row is <c>(error_id, error_created_at)</c>, where
/// <c>error_id</c> is a hash of the compact error message and <c>error_created_at</c> is truncated to
/// the day. Grouping has to use that same key: <c>ON CONFLICT DO NOTHING</c> arbitrates a row only
/// against rows that already exist, not between rows of one statement, so two rows sharing a key
/// inside a single INSERT raise a duplicate-key error.
/// </remarks>
public class ErrorDeliveryGroupingTests
{
    // ── the regression: two instances, one message ──────────────────────────

    [Fact]
    public void GroupByException_SameMessageOnSeparateInstances_CollapsesToOneRow()
    {
        // Two *distinct* instances — what any code that throws inside a loop produces.
        // Dictionary<Exception, _> treats them as different keys, but both hash to the same
        // error_id, so the old grouping put the same primary key twice into one INSERT.
        var ex1 = new InvalidOperationException("downstream 503");
        var ex2 = new InvalidOperationException("downstream 503");

        Assert.NotSame(ex1, ex2);

        var (rows, byException) = Group([
            Context(ex1, DeliveryStatusCode.Error503),
            Context(ex2, DeliveryStatusCode.Error503),
        ]);

        Assert.Single(rows);

        // Both messages still resolve to that one row, so both get task.error_id filled in.
        Assert.Equal(2, byException.Count);
        Assert.Equal(byException[ex1], byException[ex2]);
    }

    [Fact]
    public void GroupByException_SameMessage_ProducesTheSameErrorId()
    {
        var ex1 = new InvalidOperationException("downstream 503");
        var ex2 = new InvalidOperationException("downstream 503");

        var (rows, byException) = Group([
            Context(ex1, DeliveryStatusCode.Error503),
            Context(ex2, DeliveryStatusCode.Error503),
        ]);

        // The id comes from the message, not from object identity, so it is stable across instances
        // and matches what the production hash produces.
        var expected = ErrorIdOf(ex1);
        Assert.Equal(expected, byException[ex1].ErrorId);
        Assert.Equal(expected, byException[ex2].ErrorId);

        // …and the single row is keyed by exactly that id plus the day.
        Assert.Equal(new ErrorDeliveryCommand.ErrorKey(expected, byException[ex1].CreatedAt), rows.Keys.Single());
    }

    [Fact]
    public void GroupByException_SameTypeDifferentMessage_StaysSeparate()
    {
        var ex1 = new InvalidOperationException("downstream 503");
        var ex2 = new InvalidOperationException("downstream 504");

        var (rows, byException) = Group([
            Context(ex1, DeliveryStatusCode.Error503),
            Context(ex2, DeliveryStatusCode.Error504),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.NotEqual(byException[ex1].ErrorId, byException[ex2].ErrorId);
    }

    [Fact]
    public void GroupByException_SameTypeSameMessage_ButInnerChainDiffers_StaysSeparate()
    {
        // The hashed text walks the inner exception chain, so the id covers more than the top-level
        // message.
        var ex1 = new InvalidOperationException("wrapped", new TimeoutException("inner"));
        var ex2 = new InvalidOperationException("wrapped", new TimeoutException("other inner"));

        var (rows, _) = Group([
            Context(ex1, DeliveryStatusCode.Error),
            Context(ex2, DeliveryStatusCode.Error),
        ]);

        Assert.Equal(2, rows.Count);
    }

    // ── the same instance repeated ──────────────────────────────────────────

    [Fact]
    public void GroupByException_SameInstanceTwice_YieldsOneRow()
    {
        var same = new DivideByZeroException("divide");

        var (rows, byException) = Group([
            Context(same, DeliveryStatusCode.Error),
            Context(same, DeliveryStatusCode.Error),
        ]);

        Assert.Single(rows);
        Assert.Single(byException);
        Assert.Equal(ErrorIdOf(same), byException[same].ErrorId);
    }

    // ── the day component of the key ────────────────────────────────────────

    [Fact]
    public void GroupByException_SameMessageOnDifferentDays_StaysTwoRows()
    {
        // The day is part of the primary key, so collapsing on the message hash alone would drop a
        // row: a batch that spans midnight must still log the error on both days.
        var ex1 = new InvalidOperationException("downstream 503");
        var ex2 = new InvalidOperationException("downstream 503");

        var day1 = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);

        var (rows, byException) = Group([
            Context(ex1, DeliveryStatusCode.Error503, day1),
            Context(ex2, DeliveryStatusCode.Error503, day2),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), byException[ex1].CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), byException[ex2].CreatedAt);
    }

    [Fact]
    public void GroupByException_SameMessageDifferentTimesOnSameDay_CollapsesToOneRow()
    {
        // Same day, so the primary key matches even though the timestamps differ.
        var ex1 = new InvalidOperationException("downstream 503");
        var ex2 = new InvalidOperationException("downstream 503");

        var (rows, _) = Group([
            Context(ex1, DeliveryStatusCode.Error503, new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero)),
            Context(ex2, DeliveryStatusCode.Error503, new DateTimeOffset(2026, 3, 1, 23, 59, 0, TimeSpan.Zero)),
        ]);

        Assert.Single(rows);
    }

    [Fact]
    public void GroupByException_CreatedAt_IsTruncatedToStartOfDay()
    {
        var ex = new InvalidOperationException("boom");
        var createdAt = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

        var (_, byException) = Group([Context(ex, DeliveryStatusCode.Error, createdAt)]);

        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), byException[ex].CreatedAt);
    }

    // ── nothing to log ──────────────────────────────────────────────────────

    [Fact]
    public void GroupByException_NoException_ProducesNothing()
    {
        var (rows, byException) = Group([
            Context(exception: null, DeliveryStatusCode.Ok),
            Context(exception: null, DeliveryStatusCode.NoContent),
        ]);

        Assert.Empty(rows);
        Assert.Empty(byException);
    }

    [Fact]
    public void GroupByException_EmptyBatch_ProducesNothing()
    {
        var (rows, byException) = Group([]);

        Assert.Empty(rows);
        Assert.Empty(byException);
    }

    [Fact]
    public void GroupByException_NullException_DoesNotStopProcessing()
    {
        // A null exception in the middle must not cut the loop short — the error after it still counts.
        var ex = new InvalidOperationException("fail");

        var (rows, byException) = Group([
            Context(exception: null, DeliveryStatusCode.Ok),
            Context(ex, DeliveryStatusCode.Error),
            Context(exception: null, DeliveryStatusCode.Ok),
        ]);

        Assert.Single(rows);
        Assert.Equal(ErrorIdOf(ex), byException[ex].ErrorId);
    }

    [Fact]
    public void GroupByException_MixedBatch_KeepsEveryDistinctError()
    {
        var arg = new ArgumentNullException("arg");
        var arg2 = new ArgumentNullException("arg"); // distinct instance, same type + message
        var timeout = new TimeoutException("timeout");
        var io = new InvalidOperationException("wrapped", new TimeoutException("inner"));

        var (rows, byException) = Group([
            Context(arg, DeliveryStatusCode.Error),
            Context(timeout, DeliveryStatusCode.Error504),
            Context(io, DeliveryStatusCode.Error),
            Context(arg2, DeliveryStatusCode.Error),
        ]);

        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows.Keys.Distinct().Count());

        // Four messages, but the two ArgumentNullException("arg") ones share a row.
        Assert.Equal(4, byException.Count);
        Assert.Equal(byException[arg], byException[arg2]);
    }

    // ── stored metadata ─────────────────────────────────────────────────────

    [Fact]
    public void GroupByException_StoresTypeNameAndDerivedId()
    {
        var ex = new InvalidOperationException("boom");

        var (rows, byException) = Group([Context(ex, DeliveryStatusCode.Error)]);

        var row = rows.Values.Single();
        Assert.Same(ex, row.Exception);
        Assert.Equal(nameof(InvalidOperationException), row.Info.TypeName);
        Assert.Equal(ErrorIdOf(ex), row.Info.ErrorId);
        Assert.Equal(byException[ex], row.Info);
    }

    [Fact]
    public void GroupByException_StoresTheMessageOfTheFirstInstanceOfEachRow()
    {
        // The text written to error_message comes from the exception kept on the row, so identical
        // messages must produce identical text.
        var ex1 = new InvalidOperationException("downstream 503");
        var ex2 = new InvalidOperationException("downstream 503");

        var (rows, _) = Group([
            Context(ex1, DeliveryStatusCode.Error503),
            Context(ex2, DeliveryStatusCode.Error503),
        ]);

        Assert.Same(ex1, rows.Values.Single().Exception);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static (
        Dictionary<ErrorDeliveryCommand.ErrorKey, ErrorDeliveryCommand.ErrorRow> Rows,
        Dictionary<Exception, ErrorInfo> ByException)
        Group(IOutboxContext[] messages)
    {
        Dictionary<ErrorDeliveryCommand.ErrorKey, ErrorDeliveryCommand.ErrorRow> rows = new(messages.Length);
        Dictionary<Exception, ErrorInfo> byException = new(messages.Length);

        ErrorDeliveryCommand.GroupByException(messages, rows, byException);

        return (rows, byException);
    }

    private static long ErrorIdOf(Exception exception)
        => ErrorDeliveryCommand.GetErrorMessageHash(exception);

    private static IOutboxContext Context(
        Exception? exception,
        DeliveryStatusCode statusCode,
        DateTimeOffset? createdAt = null)
    {
        var now = DateTimeOffset.UtcNow;

        return new TestOutboxContext(
            OutboxId: Guid.NewGuid(),
            PayloadId: "test-payload",
            PartInfo: new OutboxPartInfo(0, "part", now),
            DeliveryInfo: new OutboxTaskDeliveryInfo(
                TaskId: 1L,
                DeliveryId: 1L,
                Attempt: 1,
                LastErrorId: 0L,
                Status: new DeliveryStatus(statusCode, string.Empty, now),
                PartInfo: new OutboxPartInfo(0, "task-part", now)),
            DeliveryResult: new DeliveryStatus(statusCode, string.Empty, createdAt ?? now),
            Exception: exception,
            PostponeDelay: TimeSpan.Zero);
    }

    /// <summary>Minimal IOutboxContext for exercising the grouping.</summary>
    private sealed class TestOutboxContext(
        Guid OutboxId,
        string PayloadId,
        OutboxPartInfo PartInfo,
        OutboxTaskDeliveryInfo DeliveryInfo,
        DeliveryStatus DeliveryResult,
        Exception? Exception,
        TimeSpan PostponeDelay) : IOutboxContext
    {
        public Guid OutboxId { get; } = OutboxId;
        public string PayloadId { get; } = PayloadId;
        public OutboxPartInfo PartInfo { get; } = PartInfo;
        public OutboxTaskDeliveryInfo DeliveryInfo { get; } = DeliveryInfo;
        public DeliveryStatus DeliveryResult { get; } = DeliveryResult;
        public Exception? Exception { get; } = Exception;
        public TimeSpan PostponeDelay { get; } = PostponeDelay;
    }
}
