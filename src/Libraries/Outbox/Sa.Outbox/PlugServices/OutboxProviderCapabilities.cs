namespace Sa.Outbox.PlugServices;

/// <summary>
/// Optional capabilities a storage provider may declare. They are documentation and diagnostics
/// first: the core changes no behaviour because of a flag, except that
/// <see cref="TenantDetection"/> requires a registered <see cref="IOutboxTenantDetector"/>.
/// </summary>
/// <remarks>
/// <see cref="FlagsAttribute"/> so a descriptor can be compared and logged compactly. The set is
/// additive: a provider that gains a capability declares one more flag, and consumers that do not
/// know the flag simply do not test it.
/// </remarks>
[Flags]
public enum OutboxProviderCapabilities
{
    /// <summary>No optional capability is declared.</summary>
    None = 0,

    /// <summary>
    /// <see cref="IOutboxBulkWriter.InsertBulk{TMessage}(OutboxMessageBatch{TMessage}, object?, CancellationToken)"/>
    /// really enlists the writes into the caller-supplied context (for example, the caller's
    /// database transaction). Without this flag a non-null context is rejected by the provider.
    /// </summary>
    PublishContext = 1 << 0,

    /// <summary>
    /// The provider registers an <see cref="IOutboxTenantDetector"/>; tenant auto-detection is
    /// therefore usable. The flag requires the detector service to be registered.
    /// </summary>
    TenantDetection = 1 << 1,

    /// <summary>
    /// <see cref="IOutboxDeliveryManager.ExtendDelivery(TimeSpan, OutboxMessageFilter, CancellationToken)"/>
    /// is meaningful: the provider renews a rented lease as the core's lock renewer asks.
    /// </summary>
    LockExtension = 1 << 2,

    /// <summary>
    /// A consumer-supplied <c>Postpone</c>/<c>Retry</c> delay is honoured by the storage. Without
    /// this flag the core still computes its own back-off for failures, but an explicit
    /// consumer-supplied delay may be ignored (the Redis provider reports this).
    /// </summary>
    Postponement = 1 << 3,
}
