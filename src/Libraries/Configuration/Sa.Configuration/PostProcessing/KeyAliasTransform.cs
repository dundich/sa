using Microsoft.Extensions.Configuration;
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
/// <para>
/// When a clients section is configured, the same mappings are also applied <b>inside</b> each
/// client's prefix: <c>sa:clients:acme:auth_base_url</c> fills
/// <c>sa:clients:acme:sa:scope:api:auth:base_url</c>. Values never cross the prefix boundary, so a
/// client override cannot leak into the base configuration or into another client.
/// </para>
/// </remarks>
public sealed class KeyAliasTransform : ISaConfigurationTransform
{
    private readonly IReadOnlyDictionary<string, string> _aliases;
    private readonly bool _canonicalValueWins;
    private readonly string? _clientsSectionPath;

    /// <summary>
    /// Creates the transform from explicit alias options.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">A mapping key or alias is blank.</exception>
    public KeyAliasTransform(KeyAliasOptions options)
        : this(options, clientsSectionPath: null)
    {
    }

    /// <summary>
    /// Creates the transform from explicit alias options, applying them inside every client prefix
    /// of <paramref name="clientsSectionPath"/> as well.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">A mapping key or alias is blank, or the section is blank.</exception>
    public KeyAliasTransform(KeyAliasOptions options, string? clientsSectionPath)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        if (clientsSectionPath is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(clientsSectionPath);

        _aliases = Normalize(options.KeyAliases);
        _canonicalValueWins = options.CanonicalValueWins;
        _clientsSectionPath = clientsSectionPath;
    }

    /// <summary>
    /// Creates the transform over an already normalized map, as accumulated by the configuration
    /// state across repeated registrations.
    /// </summary>
    internal KeyAliasTransform(
        IReadOnlyDictionary<string, string> aliases,
        bool canonicalValueWins,
        string? clientsSectionPath = null)
    {
        _aliases = aliases;
        _canonicalValueWins = canonicalValueWins;
        _clientsSectionPath = clientsSectionPath;
    }

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<string, string?>> Apply(ISaConfigurationView configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (KeyValuePair<string, string?> pair in Resolve(configuration, prefix: string.Empty))
            yield return pair;

        if (_clientsSectionPath is null)
            yield break;

        foreach (string client in FindClients(configuration, _clientsSectionPath))
        {
            string prefix = $"{_clientsSectionPath}{ConfigurationPath.KeyDelimiter}{client}{ConfigurationPath.KeyDelimiter}";

            foreach (KeyValuePair<string, string?> pair in Resolve(configuration, prefix))
                yield return pair;
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

    private IEnumerable<KeyValuePair<string, string?>> Resolve(ISaConfigurationView configuration, string prefix)
    {
        foreach (KeyValuePair<string, string> mapping in _aliases)
        {
            string canonicalKey = prefix + mapping.Key;
            string aliasKey = prefix + mapping.Value;

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

    private static IReadOnlyList<string> FindClients(ISaConfigurationView configuration, string sectionPath)
    {
        HashSet<string> clients = new(StringComparer.OrdinalIgnoreCase);
        string prefix = sectionPath + ConfigurationPath.KeyDelimiter;

        foreach (KeyValuePair<string, string?> pair in configuration.Values)
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string rest = pair.Key[prefix.Length..];
            int separator = rest.IndexOf(ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);

            if (separator > 0)
                clients.Add(rest[..separator]);
        }

        return [.. clients];
    }
}
