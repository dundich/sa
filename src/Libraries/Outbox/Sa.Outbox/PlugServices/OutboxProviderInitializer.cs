namespace Sa.Outbox.PlugServices;

/// <summary>
/// Optional provider-owned warm-up, executed once per provider before the first delivery cycle.
/// </summary>
/// <remarks>
/// <para>
/// The storage services are lazy: a data source is built without connecting, a Redis multiplexer
/// connects on the first command. Without a warm-up the first delivery tick is the one that
/// discovers an unavailable database — and the job's error policy (often
/// <c>ThenCloseApplication</c>) turns that into a host crash. This hook moves the discovery to a
/// known point, before any message is rented.
/// </para>
/// <para>
/// A provider registers it keyed by its provider key. The core memoizes the call per provider, so
/// every consumer group shares one initialization; a failed or cancelled attempt is forgotten and
/// the next tick retries it. A provider without a warm-up is valid: its first command already
/// performs the lazy connection.
/// </para>
/// <para>
/// The hook takes no token on purpose. The result is shared by every consumer group of the
/// provider, so a single caller's cancellation must not cancel it; the wait around it is what
/// observes the caller's token. Bound the warm-up with the provider's own connection/command
/// timeouts instead.
/// </para>
/// </remarks>
public interface IOutboxProviderInitializer
{
    /// <summary>
    /// Prepares the provider for delivery: checks connectivity (<c>SELECT 1</c>, <c>PING</c>) and
    /// completes any warm-up the provider needs. Must be idempotent.
    /// </summary>
    /// <returns>A task that completes when the provider is ready.</returns>
    ValueTask InitializeAsync();
}
