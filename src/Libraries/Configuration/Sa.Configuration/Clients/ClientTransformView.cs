using Microsoft.Extensions.Configuration;
using Sa.Configuration.PostProcessing;

namespace Sa.Configuration.Clients;

/// <summary>
/// The view the transform pipeline runs over for one client: the client's own rows first, the core
/// configuration as a read-only fallback.
/// </summary>
/// <remarks>
/// Only the client's rows are enumerated as <see cref="Values"/>, so a transform never rewrites a
/// core value into the client's overlay — the fallback exists so an alias can be read from the core
/// and its canonical path derived for this client. Writes land in the client's working set, which is
/// how a later transform sees an earlier one's output: the same rule the core pass follows.
/// <para>
/// <see cref="TryGetValue"/> answers <see langword="false"/> when the value is <see langword="null"/>,
/// because <see cref="IConfiguration"/> cannot tell a present null from an absent key. Every
/// built-in transform treats a null like an absent key, so the difference is not observable there.
/// </para>
/// </remarks>
internal sealed class ClientTransformView(
    Dictionary<string, string?> values,
    IConfiguration core) : ISaConfigurationView
{
    private readonly Dictionary<string, string?> _values = values;
    private readonly IConfiguration _core = core;

    public IEnumerable<KeyValuePair<string, string?>> Values => _values;

    public bool TryGetValue(string key, out string? value)
    {
        string canonical = ConfigurationKey.Normalize(key);

        if (_values.TryGetValue(canonical, out value))
            return true;

        value = _core[canonical];
        return value is not null;
    }

    /// <summary>
    /// Records a transform's output so the next transform observes it.
    /// </summary>
    public void Set(string key, string? value)
        => _values[ConfigurationKey.Normalize(key)] = value;
}
