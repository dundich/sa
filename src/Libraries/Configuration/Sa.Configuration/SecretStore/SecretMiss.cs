namespace Sa.Configuration.SecretStore;

/// <summary>
/// Describes a secret lookup that ended without a value — no store and no
/// <see cref="SecretResolutionOptions.Resolver"/> produced one.
/// </summary>
/// <param name="Key">The key taken from the <c>{{key}}</c> placeholder.</param>
/// <param name="IsOptional">
/// <see langword="true"/> when the miss is tolerated — the placeholder is <c>{{?key}}</c> or
/// <see cref="ISecretService.PopulateSecrets"/> was called with
/// <c>returnNullIfSecretNotFound: true</c> — so the caller receives <see langword="null"/>
/// instead of an exception.
/// </param>
public readonly record struct SecretMiss(string Key, bool IsOptional);
