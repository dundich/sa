namespace Sa.Configuration.PostProcessing;

/// <summary>
/// Collects the per-key rules of one <see cref="Setup.AddSaConfigurationValueTransform"/> call.
/// </summary>
/// <remarks>
/// The rules are copied when the callback returns, so the builder must not be stored and reused to
/// register more than one transform.
/// </remarks>
public sealed class SaConfigurationValueTransformBuilder
{
    private readonly Dictionary<string, Func<string?, string?>> _rules = new(StringComparer.OrdinalIgnoreCase);

    internal SaConfigurationValueTransformBuilder()
    {
    }

    /// <summary>
    /// Adds a rule: the value of <paramref name="key"/> is passed through <paramref name="map"/>.
    /// </summary>
    /// <remarks>
    /// Registering the same key twice keeps the last rule — the key is compared in the canonical
    /// <c>:</c> spelling, so <c>sa__pg__host</c> and <c>sa:pg:host</c> are the same rule. The rule
    /// runs only when the key exists; returning the received value blanks nothing and copies nothing.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="key"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    public SaConfigurationValueTransformBuilder Add(string key, Func<string?, string?> map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(map);

        // Normalized like a configuration lookup, so a rule spelled sa__pg__host matches the
        // canonical sa:pg:host the view exposes.
        _rules[ConfigurationKey.Normalize(key)] = map;
        return this;
    }

    internal IReadOnlyDictionary<string, Func<string?, string?>> Snapshot()
        => new Dictionary<string, Func<string?, string?>>(_rules, StringComparer.OrdinalIgnoreCase);
}
