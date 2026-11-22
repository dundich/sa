using Microsoft.Extensions.Configuration;
using Sa.Configuration.PostProcessing;

namespace Sa.Configuration.Clients;

/// <summary>
/// The view the transform pipeline runs over for one client: the client's own rows first, the core
/// configuration as a read-only fallback.
/// </summary>
/// <remarks>
/// Only the client's rows are exposed initially; a later transform can add keys to the working set,
/// which is how the next transform sees an earlier one's output. A value read from the core
/// fallback is tracked as such, so a per-key rule is not run a second time over a value the core
/// pass has already transformed.
/// <para>
/// <see cref="TryGetValue"/> answers <see langword="false"/> when the value is <see langword="null"/>,
/// because <see cref="IConfiguration"/> cannot tell a present null from an absent key. Every
/// built-in transform treats a null like an absent key, so the difference is not observable there.
/// </para>
/// </remarks>
internal sealed class ClientTransformView(
    Dictionary<string, string?> values,
    IConfiguration core,
    string clientId) : ISaConfigurationView, ITransformContextView
{
    private readonly Dictionary<string, string?> _values = values;
    private readonly IConfiguration _core = core;
    private readonly HashSet<string> _coreFallback = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string? ClientId { get; } = clientId;

    public IEnumerable<KeyValuePair<string, string?>> Values => _values;

    public bool TryGetValue(string key, out string? value)
    {
        string canonical = ConfigurationKey.Normalize(key);

        if (_values.TryGetValue(canonical, out value))
            return true;

        value = _core[canonical];
        return value is not null;
    }

    /// <inheritdoc />
    public bool TryGetValueWithOrigin(string key, out string? value, out bool fromCore)
    {
        string canonical = ConfigurationKey.Normalize(key);

        if (_values.TryGetValue(canonical, out value))
        {
            fromCore = _coreFallback.Contains(canonical);
            return true;
        }

        value = _core[canonical];
        fromCore = true;
        return value is not null;
    }

    /// <inheritdoc />
    public bool IsCoreFallback(string key)
        => _coreFallback.Contains(ConfigurationKey.Normalize(key));

    /// <inheritdoc />
    public bool IsOwned(string key)
    {
        string canonical = ConfigurationKey.Normalize(key);
        return _values.ContainsKey(canonical) && !_coreFallback.Contains(canonical);
    }

    /// <inheritdoc />
    public void SetOrigin(string key, bool fromCore)
    {
        string canonical = ConfigurationKey.Normalize(key);

        if (fromCore)
            _coreFallback.Add(canonical);
        else
            _coreFallback.Remove(canonical);
    }

    /// <summary>
    /// Records a transform's output so the next transform observes it. The provenance is recorded
    /// separately by <see cref="SetOrigin"/>.
    /// </summary>
    public void Set(string key, string? value)
        => _values[ConfigurationKey.Normalize(key)] = value;
}
