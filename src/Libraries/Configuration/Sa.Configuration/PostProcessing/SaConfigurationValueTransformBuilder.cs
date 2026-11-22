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
    private readonly Dictionary<string, ValueRule> _rules = new(StringComparer.OrdinalIgnoreCase);

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
    /// A value an earlier transform derived from the core fallback of a client overlay is skipped:
    /// the core pass has already transformed it. Use the context overload to handle such values.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="key"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    public SaConfigurationValueTransformBuilder Add(string key, Func<string?, string?> map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(map);

        // Normalized like a configuration lookup, so a rule spelled sa__pg__host matches the
        // canonical sa:pg:host the view exposes.
        _rules[ConfigurationKey.Normalize(key)] = new ValueRule(
            (_, value) => map(value),
            HandlesCoreFallback: false);

        return this;
    }

    /// <summary>
    /// Adds a rule that also receives the key and the layer it runs over.
    /// </summary>
    /// <remarks>
    /// This overload also receives the values an earlier transform derived from the core fallback of
    /// a client overlay (for example an alias), with
    /// <see cref="ValueTransformContext.IsCoreFallback"/> set. The rule can return the value
    /// unchanged to leave it to the core, or produce an explicit client value; a key that only falls
    /// back to the core is never passed to a rule, because it is not in the layer's values. For the
    /// root configuration <see cref="ValueTransformContext.ClientId"/> is <see langword="null"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="key"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    public SaConfigurationValueTransformBuilder Add(
        string key,
        Func<ValueTransformContext, string?, string?> map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(map);

        _rules[ConfigurationKey.Normalize(key)] = new ValueRule(map, HandlesCoreFallback: true);
        return this;
    }

    internal IReadOnlyDictionary<string, ValueRule> Snapshot()
        => new Dictionary<string, ValueRule>(_rules, StringComparer.OrdinalIgnoreCase);
}
