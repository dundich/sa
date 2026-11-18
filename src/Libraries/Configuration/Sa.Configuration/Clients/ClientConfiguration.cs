using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Sa.Configuration.Clients;

/// <summary>
/// Read-only view of one client: the client's own overlay laid over the core configuration.
/// </summary>
/// <remarks>
/// The overlay is a separate configuration root with the <b>same key names</b> as the core — there is
/// no prefix. <see cref="IConfiguration"/> lookups therefore resolve <c>s3_url</c> from the client's
/// overlay when the client has it, and fall back to the shared <c>s3_url</c> otherwise.
/// <para>
/// The view is live: it asks the registry for the current overlay on every access, so
/// <see cref="IClientConfigurations.AttachAsync"/>, <see cref="IClientConfigurations.RemoveAsync"/>
/// and a reload are visible immediately through a reference obtained earlier — including a view of
/// a client that did not exist when the reference was obtained. The change token follows the same
/// rule: <see cref="GetReloadToken"/> composes the core, the overlay and the registry, so an attach
/// or a remove signals it too.
/// </para>
/// <para>
/// Writing is not supported: configuration is assembled by providers, not by mutating a view.
/// </para>
/// </remarks>
public sealed class ClientConfiguration : IConfiguration, IConfigurationSection, IEnumerable<KeyValuePair<string, string?>>
{
    private readonly IConfiguration _core;
    private readonly Func<IConfiguration?> _overlay;
    private readonly string _path;
    private readonly string? _hiddenPath;
    private readonly Func<IChangeToken?>? _registryToken;

    /// <summary>
    /// Creates a standalone view of <paramref name="overlay"/> over <paramref name="core"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> is null.</exception>
    public ClientConfiguration(IConfiguration core, IConfiguration? overlay)
        : this(core, overlay is null ? static () => null : () => overlay, string.Empty)
    {
    }

    internal ClientConfiguration(
        IConfiguration core,
        Func<IConfiguration?> overlay,
        string path,
        string? hiddenPath = null,
        Func<IChangeToken?>? registryToken = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(overlay);

        _core = core;
        _overlay = overlay;
        _path = path;
        _hiddenPath = hiddenPath;
        _registryToken = registryToken;
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

        return new ClientConfiguration(_core, _overlay, Combine(_path, key), _hiddenPath, _registryToken);
    }

    /// <inheritdoc/>
    public IEnumerable<IConfigurationSection> GetChildren()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        // GetSection("") does not answer with the root's children, so the root level is special-cased.
        IEnumerable<IConfigurationSection> coreChildren = _path.Length == 0
            ? _core.GetChildren()
            : _core.GetSection(_path).GetChildren();

        foreach (IConfigurationSection child in coreChildren)
        {
            // The clients section is meta-data of the overlay: a client must not see its own or
            // another client's rows through the core fallback.
            if (!IsHidden(Combine(_path, child.Key)))
                names.Add(child.Key);
        }

        // The client's own children win over the core ones with the same name — the same rule a
        // lookup applies to a single value.
        if (_overlay() is { } overlay)
        {
            IEnumerable<IConfigurationSection> overlayChildren = _path.Length == 0
                ? overlay.GetChildren()
                : overlay.GetSection(_path).GetChildren();

            foreach (IConfigurationSection child in overlayChildren)
            {
                if (!IsHidden(Combine(_path, child.Key)))
                    names.Add(child.Key);
            }
        }

        return [.. names.Select(name => (IConfigurationSection)new ClientConfiguration(_core, _overlay, Combine(_path, name), _hiddenPath, _registryToken))];
    }

    /// <inheritdoc/>
    public IChangeToken GetReloadToken()
    {
        List<IChangeToken> tokens = [_core.GetReloadToken()];

        if (_overlay() is { } overlay)
            tokens.Add(overlay.GetReloadToken());

        // The registry's own token carries attach and remove, which the overlay root knows
        // nothing about — and a view taken before the overlay existed only learns about it here.
        if (_registryToken?.Invoke() is { } registryToken)
            tokens.Add(registryToken);

        return tokens.Count == 1 ? tokens[0] : new CompositeChangeToken(tokens);
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, string?>> GetEnumerator()
    {
        HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);

        if (_overlay() is { } overlay)
        {
            foreach (KeyValuePair<string, string?> pair in overlay.AsEnumerable())
            {
                if (IsHidden(pair.Key))
                    continue;

                if (TryRelative(pair.Key, _path, out string relative))
                {
                    written.Add(relative);
                    yield return new(relative, pair.Value);
                }
            }
        }

        foreach (KeyValuePair<string, string?> pair in _core.AsEnumerable())
        {
            if (IsHidden(pair.Key))
                continue;

            if (TryRelative(pair.Key, _path, out string relative) && !written.Contains(relative))
                yield return new(relative, pair.Value);
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private string? Resolve(string path)
    {
        // The clients section belongs to the overlay machinery, not to any client's settings.
        if (IsHidden(path))
            return null;

        if (_overlay() is { } overlay)
        {
            string? clientValue = overlay.GetSection(path).Value;

            // A present value (including an empty string) overrides the core one.
            if (clientValue is not null)
                return clientValue;
        }

        return _core.GetSection(path).Value;
    }

    /// <summary>
    /// Whether a full path is the hidden section or lives under it. <see langword="null"/> hides
    /// nothing; the comparison is case-insensitive, like every configuration key.
    /// </summary>
    private bool IsHidden(string fullPath)
    {
        if (_hiddenPath is not { Length: > 0 } hidden)
            return false;

        return fullPath.Equals(hidden, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(hidden + ConfigurationPath.KeyDelimiter, StringComparison.OrdinalIgnoreCase);
    }

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
