namespace Sa.Outbox.Delivery;

/// <summary>
/// Composite identity of a consumer group: the provider it belongs to plus the group id.
/// </summary>
/// <remarks>
/// A group id is unique within its provider only. With several providers in one container the same
/// id may be registered twice, so every identity-sensitive operation (settings registry, consumer DI
/// key, idle state) is addressed by this pair. The legacy <see cref="IOutboxConsumerManager"/>
/// overloads that take a bare group id keep working while the id is unambiguous and fail loudly when
/// it is not.
/// </remarks>
/// <param name="ProviderKey">The provider key (equal to the outbox scope key).</param>
/// <param name="GroupId">The consumer group id within that provider.</param>
public readonly record struct OutboxGroupKey(string ProviderKey, string GroupId)
{
    /// <inheritdoc/>
    public override string ToString() => $"{ProviderKey}/{GroupId}";
}
