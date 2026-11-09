namespace Sa.Configuration.SecretStore;

/// <summary>
/// Dynamic resolution and diagnostics for secret lookups, applied after every configured store
/// has missed.
/// </summary>
public sealed record SecretResolutionOptions
{
    /// <summary>
    /// Last-chance resolver — for example Vault, an HTTP call or a cache. Invoked only when the
    /// store chain does not contain the key.
    /// </summary>
    /// <remarks>
    /// Returning <see langword="null"/> means "not resolved": a strict lookup throws, while an
    /// optional one (<c>{{?key}}</c> or <c>returnNullIfSecretNotFound: true</c>) yields
    /// <see langword="null"/>. Exceptions from the resolver propagate unchanged.
    /// </remarks>
    public Func<string, string?>? Resolver { get; init; }

    /// <summary>
    /// Invoked during placeholder substitution when neither a store nor <see cref="Resolver"/>
    /// produced a value. Intended for logging and metrics — the secret value is never passed.
    /// </summary>
    public Action<SecretMiss>? OnUnresolvedSecret { get; init; }
}
