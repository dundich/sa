using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Sa.Configuration.PostProcessing;
using System.Collections.Concurrent;

namespace Sa.Configuration.Clients;

/// <summary>
/// Default <see cref="IClientConfigurations"/>: a registry of per-client overlays built from the
/// flat <see cref="ClientSetting"/> rows the registered providers return.
/// </summary>
/// <remarks>
/// A provider is a callback registered with
/// <see cref="SaClientConfigurationRegistration.Add{T1}(Func{T1, CancellationToken, ValueTask{IReadOnlyList{ClientSetting}}})"/>;
/// it returns rows for <b>every</b> client at once, and the registry groups them by
/// <see cref="ClientSetting.ClientId"/>. Each group becomes a separate configuration root laid over
/// the core configuration with the same key names, so <c>core["s3_url"]</c> and
/// <c>clients.GetClient("acme")["s3_url"]</c> are different values of the same field.
/// <para>
/// A client's rows are rewritten by the same transform chain the core pass runs — placeholders are
/// substituted and key aliases are resolved inside the client's own values, so a client can keep its
/// settings under the flat names its own system uses. A derived value that only mirrors the core is
/// not copied into the overlay: the client keeps reading the core live.
/// </para>
/// <para>
/// Providers are called on the first access and again on every
/// <see cref="ReloadAsync"/>; a client added to the provider becomes addressable without a restart.
/// The registry is thread-safe, and a view obtained earlier follows the current overlay.
/// </para>
/// </remarks>
public sealed class ClientConfigurations : IClientConfigurations, IDisposable
{
    private readonly IConfiguration _core;
    private readonly ClientConfigurationOptions _options;
    private readonly string? _hiddenSectionPath;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RowLoader> _loaders = [];
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _loadersGate = new();
    private readonly object _tokenGate = new();

    private Dictionary<string, Dictionary<string, string?>> _rows = new(StringComparer.OrdinalIgnoreCase);
    private IServiceProvider _services = EmptyServiceProvider.Instance;
    private Func<IReadOnlyList<ISaConfigurationTransform>>? _transforms;
    private CancellationTokenSource _reloadCts = new();
    private bool _loaded;
    private int _disposed;

    /// <summary>
    /// Creates an empty registry over <paramref name="core"/>. Clients can be attached at runtime;
    /// a registry built by dependency injection also has the registered providers.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> is null.</exception>
    public ClientConfigurations(IConfiguration core)
        : this(core, new ClientConfigurationOptions())
    {
    }

    internal ClientConfigurations(IConfiguration core, ClientConfigurationOptions options, string? hiddenSectionPath = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(options);

        _core = core;
        _options = options;
        _hiddenSectionPath = hiddenSectionPath;
        _transforms = FindTransforms(core);
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Clients
    {
        get
        {
            ThrowIfDisposed();
            EnsureLoaded();

            HashSet<string> clients;

            lock (_loadersGate)
            {
                clients = new HashSet<string>(_rows.Keys, StringComparer.OrdinalIgnoreCase);
            }

            // Attached clients are not part of any provider's rows.
            foreach (KeyValuePair<string, Entry> pair in _entries)
            {
                if (pair.Value.IsAttached)
                    clients.Add(pair.Key);
            }

            return [.. clients];
        }
    }

    /// <inheritdoc/>
    public IConfiguration GetClient(string client)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(client);
        ThrowIfDisposed();
        EnsureLoaded();

        // A runtime attachment wins over anything a provider reported for the same client.
        if (IsAttached(client))
            return View(client);

        bool known;

        // The entry is created while the rows are read: a reload that finished in between would
        // otherwise drop the client and the entry would resurrect it from the previous snapshot.
        lock (_loadersGate)
        {
            known = _rows.TryGetValue(client, out Dictionary<string, string?>? values);

            if (known)
                _entries.GetOrAdd(client, _ => CreateRowEntry(values!, ActiveTransforms()));
        }

        if (!known && _options.ThrowForUnknownClient)
            throw new InvalidOperationException($"The client '{client}' is not registered or discovered.");

        // The view always goes through the registry, so an unknown client reads the core values and
        // still sees an overlay attached to it later through the same reference.
        return View(client);
    }

    /// <inheritdoc/>
    public bool TryGet(string client, out IConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(client);
        ThrowIfDisposed();
        EnsureLoaded();

        if (IsAttached(client))
        {
            configuration = View(client);
            return true;
        }

        bool known;

        lock (_loadersGate)
        {
            known = _rows.ContainsKey(client);
        }

        // GetClient throws for an unknown client when ThrowForUnknownClient is set; a Try method
        // reports instead of throwing, so the view is built here.
        configuration = known ? GetClient(client) : View(client);

        return known;
    }

    /// <inheritdoc/>
    public Task AttachAsync(string client, IConfigurationSource source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(client);
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        ConfigurationBuilder builder = new();
        builder.Add(source);

        Entry replacement = new(builder.Build(), values: null);
        Entry? previous = null;

        // AddOrUpdate answers with the value it stored, so the replaced entry has to be captured in
        // the factory: disposing the return value would dispose the root that was just attached.
        _entries.AddOrUpdate(
            client,
            replacement,
            (_, current) =>
            {
                previous = current;
                return replacement;
            });

        DisposeEntry(previous);
        RaiseChanged();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveAsync(string client, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(client);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        while (true)
        {
            // Only a runtime attachment can be removed. A client a provider reports comes back with
            // the next access, so removing its entry would be a no-op that lies about the outcome.
            if (!_entries.TryGetValue(client, out Entry? entry) || !entry.IsAttached)
                return Task.FromResult(false);

            // Conditional removal: an attach racing on the same client must not be disposed here.
            if (!_entries.TryRemove(new KeyValuePair<string, Entry>(client, entry)))
                continue;

            DisposeEntry(entry);
            RaiseChanged();

            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public async ValueTask ReloadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Dictionary<string, Dictionary<string, string?>> rows = await LoadRowsAsync(_services, cancellationToken)
                .ConfigureAwait(false);

            lock (_loadersGate)
            {
                _rows = rows;
                _loaded = true;
            }

            ApplyRows(rows, cancellationToken);
        }
        finally
        {
            _loadGate.Release();
        }

        RaiseChanged();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Does not throw after <see cref="Dispose"/>: the token comes back already canceled, so a
    /// consumer that registers during shutdown is signaled instead of receiving an
    /// <see cref="ObjectDisposedException"/> through a view's composite token.
    /// </remarks>
    public IChangeToken GetReloadToken()
    {
        lock (_tokenGate)
        {
            return new CancellationChangeToken(_reloadCts.Token);
        }
    }

    /// <summary>Disposes every client root created by this registry.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        foreach (Entry entry in _entries.Values)
            DisposeEntry(entry);

        _entries.Clear();

        lock (_tokenGate)
        {
            // Cancelled, not disposed: views hold composite tokens over this source, and a token
            // whose source was disposed throws on a late Register. A cancelled source is inert
            // and is collected with the registry.
            _reloadCts.Cancel();
        }

        _loadGate.Dispose();
    }

    internal void SetServices(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
    }

    /// <summary>
    /// Points the registry at the transform chain the core pass runs, so a client overlay is
    /// rewritten by the same rules. Called by the runtime, which owns the pass.
    /// </summary>
    internal void SetTransforms(Func<IReadOnlyList<ISaConfigurationTransform>> transforms)
    {
        ArgumentNullException.ThrowIfNull(transforms);

        _transforms = transforms;
    }

    internal void AddLoader(RowLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ThrowIfDisposed();

        lock (_loadersGate)
        {
            _loaders.Add(loader);
            _loaded = false;
        }
    }

    private ClientConfiguration View(string client)
        => new(_core, () => ResolveOverlay(client), string.Empty, _hiddenSectionPath, ViewRegistryToken);

    private bool IsAttached(string client)
        => _entries.TryGetValue(client, out Entry? entry) && entry.IsAttached;

    private IConfiguration? ResolveOverlay(string client)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return null;

        if (_entries.TryGetValue(client, out Entry? entry))
            return entry.Root;

        // A view obtained while the client was unknown has to see it after the reload that
        // discovered it, so the entry is created here on first read — the same way GetClient does
        // it. Without this the stale view and a fresh GetClient would answer differently.
        lock (_loadersGate)
        {
            if (_rows.TryGetValue(client, out Dictionary<string, string?>? values))
                return _entries.GetOrAdd(client, _ => CreateRowEntry(values, ActiveTransforms())).Root;
        }

        return null;
    }

    /// <summary>
    /// The registry's change token for a view, or <see langword="null"/> once the registry is
    /// disposed — a view must keep answering during shutdown instead of throwing.
    /// </summary>
    private IChangeToken? ViewRegistryToken()
        => Volatile.Read(ref _disposed) == 1 ? null : GetReloadToken();

    private void EnsureLoaded()
    {
        if (Volatile.Read(ref _disposed) == 1)
            return;

        _loadGate.Wait();

        try
        {
            lock (_loadersGate)
            {
                if (_loaded)
                    return;
            }

            Dictionary<string, Dictionary<string, string?>> rows = LoadRowsAsync(_services, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            lock (_loadersGate)
            {
                _rows = rows;
                _loaded = true;
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<Dictionary<string, Dictionary<string, string?>>> LoadRowsAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        RowLoader[] loaders;

        lock (_loadersGate)
        {
            loaders = [.. _loaders];
        }

        Dictionary<string, Dictionary<string, string?>> rows = new(StringComparer.OrdinalIgnoreCase);

        foreach (RowLoader loader in loaders)
        {
            IReadOnlyList<ClientSetting> settings = await loader(services, cancellationToken)
                .ConfigureAwait(false);

            foreach (ClientSetting setting in settings)
            {
                // The same rule the core database layer applies: a blank row must not blank the
                // setting for the client — it delegates to the core value instead. Blanks a
                // transform produced are written after this point and are not affected.
                if (_options.SkipEmptyValues && string.IsNullOrWhiteSpace(setting.Value))
                    continue;

                if (!rows.TryGetValue(setting.ClientId, out Dictionary<string, string?>? values))
                    rows[setting.ClientId] = values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                // The same canonical spelling the core pass uses, so `sa__pg__host` and
                // `sa:pg:host` are one setting here too.
                values[ConfigurationKey.Normalize(setting.Key)] = setting.Value;
            }
        }

        return rows;
    }

    private void ApplyRows(
        Dictionary<string, Dictionary<string, string?>> rows,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ISaConfigurationTransform>? transforms = ActiveTransforms();

        foreach (KeyValuePair<string, Entry> pair in _entries)
        {
            Entry entry = pair.Value;

            // An attached root owns its providers; it is refreshed by its own reload.
            if (entry.Values is not { } provider)
            {
                entry.Root.Reload();
                continue;
            }

            if (rows.TryGetValue(pair.Key, out Dictionary<string, string?>? values))
            {
                provider.Replace(BuildData(values, transforms));
                entry.Root.Reload();
                continue;
            }

            if (_entries.TryRemove(pair.Key, out Entry? removed))
                DisposeEntry(removed);
        }

        if (!_options.EagerLoad)
            return;

        foreach (string client in rows.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries.GetOrAdd(client, _ => CreateRowEntry(rows[client], transforms));
        }
    }

    /// <summary>
    /// Rewrites one client's rows through the transform chain the core pass runs: placeholders are
    /// substituted and key aliases are resolved against the client's own values first and the core
    /// values second.
    /// </summary>
    /// <remarks>
    /// A derived value is kept only when it differs from the core, so the overlay holds what is
    /// specific to the client and everything else keeps reading the core live instead of freezing a
    /// snapshot of it. A value the pass blanked (an unresolved placeholder) stays local to the
    /// client, because <c>null</c> in the overlay would silently fall back to the core value.
    /// </remarks>
    private Dictionary<string, string?> BuildData(
        IReadOnlyDictionary<string, string?> rows,
        IReadOnlyList<ISaConfigurationTransform>? transforms)
    {
        Dictionary<string, string?> data = new(rows, StringComparer.OrdinalIgnoreCase);

        if (transforms is not { Count: > 0 })
            return data;

        ClientTransformView view = new(
            new Dictionary<string, string?>(rows, StringComparer.OrdinalIgnoreCase),
            _core);

        foreach (ISaConfigurationTransform transform in transforms)
        {
            // Snapshot: writing to the view is how the next transform sees this one's output.
            foreach (KeyValuePair<string, string?> change in transform.Apply(view).ToArray())
            {
                string key = ConfigurationKey.Normalize(change.Key);
                view.Set(key, change.Value);

                bool own = rows.ContainsKey(key);

                if (change.Value is null)
                {
                    // An unresolved placeholder must not leak as a literal, and it must not fall
                    // through to the core value either: the client's blank stays the client's.
                    if (own)
                        data[key] = string.Empty;

                    continue;
                }

                if (own || !string.Equals(_core[key], change.Value, StringComparison.Ordinal))
                    data[key] = change.Value;
            }
        }

        return data;
    }

    private IReadOnlyList<ISaConfigurationTransform>? ActiveTransforms() => _transforms?.Invoke();

    /// <summary>
    /// Finds the core pass on the builder the client registry was registered with, so the transforms
    /// apply even when no runtime is registered. The runtime attaches them otherwise.
    /// </summary>
    private static Func<IReadOnlyList<ISaConfigurationTransform>>? FindTransforms(IConfiguration core)
        => core is IConfigurationBuilder builder && SaConfigurationState.Find(builder) is { } state
            ? state.BuildTransforms
            : null;

    private Entry CreateRowEntry(
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyList<ISaConfigurationTransform>? transforms)
    {
        ClientValuesProvider provider = new(BuildData(values, transforms));

        return new Entry(new ConfigurationRoot([provider]), provider);
    }

    private static void DisposeEntry(Entry? entry)
        => (entry?.Root as IDisposable)?.Dispose();

    private void RaiseChanged()
    {
        CancellationTokenSource previous;

        lock (_tokenGate)
        {
            previous = _reloadCts;
            _reloadCts = new CancellationTokenSource();
        }

        // Cancelled, not disposed — see Dispose: a view may hold a composite token over this
        // source and register a callback a moment later.
        previous.Cancel();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    private sealed class Entry(IConfigurationRoot root, ClientValuesProvider? values)
    {
        public IConfigurationRoot Root { get; } = root;

        /// <summary>Non-null for a provider-backed entry, null for a runtime attachment.</summary>
        public ClientValuesProvider? Values { get; } = values;

        /// <summary>Whether the entry came from <see cref="AttachAsync"/> rather than a provider.</summary>
        public bool IsAttached => Values is null;
    }

    /// <summary>
    /// Holds one client's grouped rows. The registry pushes a fresh snapshot on every reload, so a
    /// client view reads current values without rebuilding the root.
    /// </summary>
    private sealed class ClientValuesProvider : ConfigurationProvider
    {
        public ClientValuesProvider(IReadOnlyDictionary<string, string?> values) => Replace(values);

        public void Replace(IReadOnlyDictionary<string, string?> values)
            => Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);

        // The registry owns the data; a provider reload must not read anything on its own.
        public override void Load()
        {
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
