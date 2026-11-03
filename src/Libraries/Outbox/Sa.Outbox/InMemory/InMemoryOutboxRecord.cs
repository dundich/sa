using Sa.Outbox.Delivery;

namespace Sa.Outbox.InMemory;

/// <summary>
/// One stored message plus its delivery state.
/// </summary>
/// <remarks>
/// Mutable on purpose: the store owns the instance and flips its state under its own lock. The record
/// is the in-memory equivalent of the PostgreSQL task row — message, lease and attempt counter in one
/// place — which is what makes the provider a readable example of the delivery contract.
/// </remarks>
internal sealed class InMemoryOutboxRecord
{
    /// <summary>The internal id; monotonic per store and used as <see cref="OutboxRentToken.Key"/>.</summary>
    public long Id { get; set; }

    /// <summary>The provider's public message identity.</summary>
    public Guid OutboxId { get; init; } = Guid.CreateVersion7();

    public int TenantId { get; init; }

    public string Part { get; init; } = string.Empty;

    /// <summary>The payload type identity the pipeline filters on (<see cref="Type.Name"/>).</summary>
    public string PayloadType { get; init; } = string.Empty;

    public string PayloadId { get; init; } = string.Empty;

    public byte[] Payload { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DeliveryStatusCode Status { get; set; } = DeliveryStatusCode.Pending;

    public string? StatusMessage { get; set; }

    /// <summary>How many times the message was delivered; advanced on <c>Retry</c>/<c>Warn</c>.</summary>
    public int Attempt { get; set; }

    /// <summary>When the message becomes rentable again after a postponement.</summary>
    public DateTimeOffset? AvailableAt { get; set; }

    /// <summary>When the current lease expires; <c>null</c> when the message is not rented.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>The rent generation; the second half of <see cref="OutboxRentToken"/>.</summary>
    public long RentGeneration { get; set; }

    /// <summary>The owner of the current lease (<see cref="OutboxMessageFilter.TransactId"/>).</summary>
    public string? TransactId { get; set; }
}
