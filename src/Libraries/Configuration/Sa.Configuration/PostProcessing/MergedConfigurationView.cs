namespace Sa.Configuration.PostProcessing;

/// <summary>
/// Mutable, canonical view over the merged configuration that the post-processing pass builds
/// once per <c>Load</c>. Every write from a transform lands here first, so later transforms see
/// it, and only what transforms actually wrote is copied into the provider's own data.
/// </summary>
internal sealed class MergedConfigurationView(Dictionary<string, string?> values)
    : ISaConfigurationView, ITransformContextView
{
    public IEnumerable<KeyValuePair<string, string?>> Values => values;

    /// <summary>The root configuration has no client.</summary>
    public string? ClientId => null;

    public bool TryGetValue(string key, out string? value)
        => values.TryGetValue(ConfigurationKey.Normalize(key), out value);

    public bool TryGetValueWithOrigin(string key, out string? value, out bool fromCore)
    {
        fromCore = false;
        return TryGetValue(key, out value);
    }

    /// <summary>The root pass owns every key it sees.</summary>
    public bool IsCoreFallback(string key) => false;

    /// <summary>The root pass owns every key it sees.</summary>
    public bool IsOwned(string key) => true;

    /// <summary>Records a transform's output so the next transform observes it. The key is normalized
    /// the same way lookups are, which keeps the working set to one entry per setting.</summary>
    public void Set(string key, string? value)
        => values[ConfigurationKey.Normalize(key)] = value;

    /// <summary>The root pass has no core fallback, so there is no provenance to record.</summary>
    public void SetOrigin(string key, bool fromCore)
    {
    }
}
