namespace Sa.Outbox.Publication;

/// <summary>
/// Immutable settings for publishing messages from the Outbox.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MaxBatchSize"/> is the chunk size of one <c>COPY</c>, i.e. of one publish transaction.
/// It is also the amortization window of the per-<c>(tenant, part)</c> publish lock: that lock
/// serializes publishes of the same part (which is what keeps <c>msg_seq</c> order equal to commit
/// order), so a small chunk pays the lock once per few rows. Measured with 8 parallel publishers:
/// the lock costs −75 % of throughput at batch 16, −24 % at 64, and ~0 % at 256 and above, because
/// from there the database is already the serialization point.
/// </para>
/// <para>
/// The value must be greater than zero. A zero chunk would make the publisher return success while
/// writing nothing, and a negative one fails inside <c>ArrayPool.Rent</c>; both are rejected here so
/// a misconfiguration surfaces at the registration that introduced it.
/// </para>
/// </remarks>
public sealed record OutboxPublishSettings
{
    private int _maxBatchSize;

    /// <summary>Creates settings with the given publish chunk size (default 256).</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="MaxBatchSize"/> is not greater than zero.
    /// </exception>
    public OutboxPublishSettings(int MaxBatchSize = 256)
        => this.MaxBatchSize = MaxBatchSize;

    /// <summary>The number of messages written per publish transaction.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the assigned value is not greater than zero.
    /// </exception>
    public int MaxBatchSize
    {
        get => _maxBatchSize;
        init => _maxBatchSize = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "MaxBatchSize must be greater than 0.");
    }

    /// <summary>
    /// Creates a copy with a new <see cref="MaxBatchSize"/>.
    /// </summary>
    public OutboxPublishSettings WithMaxBatchSize(int batchSize)
        => this with { MaxBatchSize = batchSize };
}
