using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Sa.Configuration.Clients;

/// <summary>
/// Read-only view of one client's configuration: the client's overrides laid over the base
/// (non-keyed) configuration.
/// </summary>
/// <remarks>
/// Client data lives under <c>{section}:{client}:{key}</c> in <b>any</b> source — appsettings,
/// environment variables, command-line arguments or a database — because the overlay is a key
/// prefix, not a separate provider. <see cref="IConfiguration"/> lookups therefore resolve
/// <c>theme</c> as <c>clients:acme:theme</c> when the client has it and fall back to the shared
/// <c>theme</c> otherwise.
/// <para>
/// The view is live: it reads the underlying root on every access, so a reload of the dynamic layer
/// is visible immediately and no per-client invalidation is needed. Clients never see each other's
/// values — each view only reads its own prefix.
/// </para>
/// <para>
/// Writing is not supported: configuration is assembled by providers, not by mutating a view.
/// </para>
/// </remarks>
public sealed class ClientConfiguration : IConfiguration, IConfigurationSection, IEnumerable<KeyValuePair<string, string?>>
{
    /// <summary>Section that holds per-client overrides. Override with <c>UseClients</c>.</summary>
    public const string DefaultSectionPath = "sa:clients";

    private readonly IConfiguration _root;
    private readonly string _section;
    private readonly string _client;
    private readonly string _path;

    /// <summary>
    /// Creates the view of <paramref name="client"/> over <paramref name="root"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="client"/> is blank.</exception>
    public ClientConfiguration(IConfiguration root, string client, string sectionPath = DefaultSectionPath)
        : this(root, sectionPath, client, string.Empty)
    {
    }

    private ClientConfiguration(IConfiguration root, string section, string client, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(client);

        _root = root;
        _section = section;
        _client = client;
        _path = path;
    }

    /// <inheritdoc/>
    public string Key
    {
        get
        {
            int index = _path.LastIndexOf(ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
            return index < 0 ? _path : _path[(index + 1)..];
        }
    }

    /// <inheritdoc/>
    public string Path => _path;

    /// <inheritdoc/>
    public string? Value
    {
        get => _path.Length == 0 ? null : Resolve(_path);
        set => throw new NotSupportedException("A client configuration view is read-only.");
    }

    /// <inheritdoc/>
    public string? this[string key]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(key);
            return Resolve(Combine(_path, key));
        }
        set => throw new NotSupportedException("A client configuration view is read-only.");
    }

    /// <inheritdoc/>
    public IConfigurationSection GetSection(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return new ClientConfiguration(_root, _section, _client, Combine(_path, key));
    }

    /// <inheritdoc/>
    public IEnumerable<IConfigurationSection> GetChildren()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        // GetSection("") does not answer with the root's children, so the root level is special-cased.
        IEnumerable<IConfigurationSection> baseChildren = _path.Length == 0
            ? _root.GetChildren()
            : _root.GetSection(_path).GetChildren();

        foreach (IConfigurationSection child in baseChildren)
        {
            // The overrides section itself is meta-data: a client must not see other clients' keys.
            if (IsOverridesPath(Combine(_path, child.Key)))
                continue;

            names.Add(child.Key);
        }

        foreach (IConfigurationSection child in _root.GetSection(ClientPath(_path)).GetChildren())
            names.Add(child.Key);

        return [.. names.Select(name => (IConfigurationSection)new ClientConfiguration(_root, _section, _client, Combine(_path, name)))];
    }

    /// <inheritdoc/>
    public IChangeToken GetReloadToken() => _root.GetReloadToken();

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, string?>> GetEnumerator()
    {
        string clientPrefix = ClientPath(_path);
        string basePrefix = _path;

        HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);

        // Client values first: they win over the base value of the same path.
        foreach (KeyValuePair<string, string?> pair in _root.AsEnumerable())
        {
            if (TryRelative(pair.Key, clientPrefix, out string relative))
            {
                written.Add(relative);
                yield return new(relative, pair.Value);
            }
        }

        foreach (KeyValuePair<string, string?> pair in _root.AsEnumerable())
        {
            if (TryRelative(pair.Key, basePrefix, out string relative)
                && !written.Contains(relative)
                && !IsOverridesPath(relative))
            {
                yield return new(relative, pair.Value);
            }
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private string ClientPath(string path)
        => path.Length == 0
            ? $"{_section}{ConfigurationPath.KeyDelimiter}{_client}"
            : $"{_section}{ConfigurationPath.KeyDelimiter}{_client}{ConfigurationPath.KeyDelimiter}{path}";

    private string? Resolve(string path)
    {
        IConfigurationSection clientSection = _root.GetSection(ClientPath(path));

        // A present value (including an empty string) overrides the base one.
        if (clientSection.Value is not null)
            return clientSection.Value;

        return _root.GetSection(path).Value;
    }

    /// <summary>
    /// Whether a base path belongs to the overrides section (the section itself or anything under
    /// it). Such keys are meta-data of the overlay and are hidden from a client view.
    /// </summary>
    private bool IsOverridesPath(string path)
        => path.Equals(_section, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(_section + ConfigurationPath.KeyDelimiter, StringComparison.OrdinalIgnoreCase);

    private static string Combine(string prefix, string path)
    {
        if (prefix.Length == 0)
            return path;

        return path.Length == 0
            ? prefix
            : $"{prefix}{ConfigurationPath.KeyDelimiter}{path}";
    }

    private static bool TryRelative(string key, string prefix, out string relative)
    {
        if (prefix.Length == 0)
        {
            relative = key;
            return true;
        }

        if (key.StartsWith(prefix + ConfigurationPath.KeyDelimiter, StringComparison.OrdinalIgnoreCase))
        {
            relative = key[(prefix.Length + 1)..];
            return true;
        }

        relative = string.Empty;
        return false;
    }
}
