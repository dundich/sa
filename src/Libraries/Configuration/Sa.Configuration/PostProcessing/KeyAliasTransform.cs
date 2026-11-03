using Sa.Classes;
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
/// </remarks>
public sealed class KeyAliasTransform : ISaConfigurationTransform
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
    internal KeyAliasTransform(IReadOnlyDictionary<string, string> aliases, bool canonicalValueWins)
    {
        _aliases = aliases;
        _canonicalValueWins = canonicalValueWins;
    }

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<string, string?>> Apply(ISaConfigurationView configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (KeyValuePair<string, string> mapping in _aliases)
        {
            string canonicalKey = mapping.Key;
            string aliasKey = mapping.Value;

            if (!configuration.TryGetValue(aliasKey, out string? aliasValue)
                || string.IsNullOrWhiteSpace(aliasValue))
            {
                continue;
            }

            if (_canonicalValueWins
                && configuration.TryGetValue(canonicalKey, out string? canonicalValue)
                && !string.IsNullOrWhiteSpace(canonicalValue))
            {
                continue;
            }

            yield return new(canonicalKey, aliasValue);
        }
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
}
