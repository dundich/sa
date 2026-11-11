namespace Sa.Outbox;

/// <summary>
/// Represents a filter for querying Outbox messages based on specific criteria.
/// This record is used to define the parameters for filtering messages in the Outbox.
/// </summary>
public sealed record OutboxMessageFilter
{
    /// <summary>The transaction that owns the rented messages.</summary>
    public required string TransactId { get; init; }

    /// <summary>The consumer group the filter was built for.</summary>
    public required string ConsumerGroupId { get; init; }

    /// <summary>The payload type name the pipeline filters on.</summary>
    public required string PayloadType { get; init; }

    /// <summary>The tenant the messages belong to.</summary>
    public int TenantId { get; init; }

    /// <summary>The part identifier of the outbox message.</summary>
    public required string Part { get; init; }

    /// <summary>Read messages created on or after this instant.</summary>
    public DateTimeOffset FromDate { get; init; }

    /// <summary>Read messages created before this instant.</summary>
    public DateTimeOffset ToDate { get; init; }

    /// <summary>The wall clock at call time.</summary>
    public DateTimeOffset NowDate { get; init; }

    /// <summary>The provider key of the storage this filter targets.</summary>
    public string ProviderKey { get; init; } = OutboxScope.DefaultKey;
}
