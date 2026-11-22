using Sa.Configuration.SecretStore;

namespace Sa.Configuration.PostProcessing;

/// <summary>
/// Fills a canonical configuration path from an alternative spelling of the same setting, e.g.
/// <c>sa:scope:api:auth:base_url</c> from the flat <c>auth_base_url</c>. See
/// <see cref="KeyAliasOptions"/> for the semantics.
/// </summary>
/// <remarks>
/// Returns only the canonical paths it fills — the alias key itself is never copied, so two
/// transforms (or two <c>AddSaConfiguration</c> calls) with different mappings compose instead
/// of overwriting each other. Mappings are compared case-insensitively and with <c>__</c>
/// normalized to <c>:</c>, exactly like configuration keys.
/// <para>
/// In a client overlay an alias derived from a core value is reported as a core fallback: the core
/// pass has already built that canonical path, so the value must not be frozen into the client
/// overlay or fed to a per-key rule a second time. An alias derived from the client's own row stays
/// owned by the client.
/// </para>
/// </remarks>
public sealed class KeyAliasTransform : ISaConfigurationTransform, IOriginTransform
{
    private readonly IReadOnlyDictionary<string, string> _aliases;
    private readonly bool _canonicalValueWins;

    /// <summary>
    /// Creates the transform from explicit alias options.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">A mapping key or alias is blank.</exception>
    public KeyAliasTransform(KeyAliasOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        _aliases = Normalize(options.KeyAliases);
        _canonicalValueWins = options.CanonicalValueWins;
    }

    /// <summary>
    /// Creates the transform over an already normalized map, as accumulated by the configuration
    /// state across repeated registrations.
    /// </summary>
    internal KeyAliasTransform(
        IReadOnlyDictionary<string, string> aliases,
        bool canonicalValueWins)
    {
        _aliases = aliases;
        _canonicalValueWins = canonicalValueWins;
    }

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<string, string?>> Apply(ISaConfigurationView configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return Resolve(configuration)
            .Select(static change => new KeyValuePair<string, string?>(change.Key, change.Value));
    }

    /// <inheritdoc/>
    IEnumerable<TransformChange> IOriginTransform.ApplyWithOrigin(ISaConfigurationView configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return Resolve(configuration);
    }

    /// <summary>
    /// Normalizes a mapping into canonical spelling. The result is case-insensitive, so two
    /// spellings of the same path collapse into one entry — the last one registered wins, which
    /// mirrors how repeated registrations merge.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Normalize(IDictionary<string, string> aliases)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> mapping in aliases)
        {
            result[ConfigurationKey.Normalize(mapping.Key)] = ConfigurationKey.Normalize(mapping.Value);
        }

        return result;
    }

    private IEnumerable<TransformChange> Resolve(ISaConfigurationView configuration)
    {
        foreach (KeyValuePair<string, string> mapping in _aliases)
        {
            string canonicalKey = mapping.Key;
            string aliasKey = mapping.Value;

            if (!TryGetValue(configuration, aliasKey, out string? aliasValue, out bool fromCore)
                || string.IsNullOrWhiteSpace(aliasValue))
            {
                continue;
            }

            if (_canonicalValueWins
                && TryGetValue(configuration, canonicalKey, out string? canonicalValue, out _)
                && !string.IsNullOrWhiteSpace(canonicalValue))
            {
                continue;
            }

            // A value derived from the core must not overwrite a key the client owns: the client's
            // own value is the more specific one.
            if (fromCore
                && configuration is ITransformContextView contextView
                && contextView.IsOwned(canonicalKey))
            {
                continue;
            }

            yield return new TransformChange(canonicalKey, aliasValue, fromCore);
        }
    }

    private static bool TryGetValue(
        ISaConfigurationView configuration,
        string key,
        out string? value,
        out bool fromCore)
    {
        if (configuration is ITransformContextView contextView)
            return contextView.TryGetValueWithOrigin(key, out value, out fromCore);

        fromCore = false;
        return configuration.TryGetValue(key, out value);
    }
}
