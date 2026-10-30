namespace Sa.Configuration.SecretStore;

/// <summary>
/// Explicit key aliases: <c>canonical C# key → alternative spelling</c>, e.g.
/// <c>sa:scope:api:auth:base_url → auth_base_url</c> when a setting is stored under the flat
/// PHP spelling while the C# code reads it as a nested path.
/// </summary>
/// <remarks>
/// Applied at the final post-secrets-processing stage, once all <c>IConfiguration</c> providers
/// are merged: a non-blank alias value is written to the canonical path, or only to an empty or
/// absent canonical path when <see cref="CanonicalValueWins"/> is set. A missing or blank alias
/// value never touches the canonical path, and the alias key itself is left in place, so the
/// flat spelling stays readable directly.
/// Mapping keys are compared the same way configuration keys are — case-insensitively, and
/// spelled with <c>:</c> when they contain <c>__</c> (the environment-variable spelling of the
/// separator), so <c>sa__scope__api__auth__base_url</c> in the map addresses the same key as
/// <c>sa:scope:api:auth:base_url</c>.
/// An empty or blank mapping entry is a registration-time error: such an alias can never match
/// a setting, so it fails while the host is still building rather than silently doing nothing.
/// </remarks>
public sealed record KeyAliasOptions
{
    /// <summary>
    /// Explicit mappings from the canonical C# configuration path to an alternative spelling of
    /// the same setting. Empty by default: an empty map leaves the load pipeline exactly as it
    /// was before the feature.
    /// </summary>
    public IDictionary<string, string> KeyAliases { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// When <see langword="true"/>, a non-blank alias value fills the canonical path only when it
    /// is empty or absent in the merged configuration — a value any lower-priority provider
    /// already filled wins over the alias. Default (<see langword="false"/>) is the opposite:
    /// the non-blank alias value always wins for the canonical path.
    /// </summary>
    public bool CanonicalValueWins { get; init; }

    internal void Validate()
    {
        foreach (KeyValuePair<string, string> mapping in KeyAliases)
        {
            if (string.IsNullOrWhiteSpace(mapping.Key))
                throw new ArgumentException(
                    $"{nameof(KeyAliases)}: every canonical key must be non-blank, but an empty mapping key was found.");

            if (string.IsNullOrWhiteSpace(mapping.Value))
                throw new ArgumentException(
                    $"{nameof(KeyAliases)}: the mapping '{mapping.Key} → …' must not have a blank alias key.");
        }
    }
}
