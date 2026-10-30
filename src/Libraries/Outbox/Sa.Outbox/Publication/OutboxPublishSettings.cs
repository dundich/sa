namespace Sa.Outbox.Publication;

/// <summary>
/// Immutable settings for publishing messages from the Outbox.
/// </summary>
/// <remarks>
/// <see cref="MaxBatchSize"/> is the chunk size of one <c>COPY</c>, i.e. of one publish transaction.
/// It is also the amortization window of the per-<c>(tenant, part)</c> publish lock: that lock
/// serializes publishes of the same part (which is what keeps <c>msg_seq</c> order equal to commit
/// order), so a small chunk pays the lock once per few rows. Measured with 8 parallel publishers:
/// the lock costs −75 % of throughput at batch 16, −24 % at 64, and ~0 % at 256 and above, because
/// from there the database is already the serialization point.
/// </remarks>
public sealed record OutboxPublishSettings(int MaxBatchSize = 256)
{
    /// <summary>
    /// Creates a copy with a new <see cref="MaxBatchSize"/>.
    /// </summary>
    public OutboxPublishSettings WithMaxBatchSize(int batchSize)
        => this with { MaxBatchSize = batchSize };
}
