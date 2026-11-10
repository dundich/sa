using Microsoft.Extensions.Configuration;

namespace Sa.Configuration;

/// <summary>
/// Per-configuration registry of the layers Sa knows how to assemble: the dynamic providers, the
/// post-processing pass and the late-bound initializers.
/// </summary>
/// <remarks>
/// The instance lives in <see cref="IConfigurationBuilder.Properties"/> under
/// <see cref="PropertyKey"/>, so every source and extension that receives the builder reaches the
/// same registry — including a source that only gets the builder in
/// <see cref="IConfigurationSource.Build"/>. The registry carries no configuration values itself:
/// it only holds provider references, which keeps it valid across reloads.
/// </remarks>
public sealed class SaConfigurationRuntime : ISaConfigurationReloader, IDisposable
{
    /// <summary>Key the runtime is stored under in the builder's property bag.</summary>
    public const string PropertyKey = "Sa.Configuration.Runtime";

    private readonly List<IConfigurationProvider> _dynamic = [];
    private readonly List<IConfigurationProvider> _postProcessing = [];
    private readonly List<Func<CancellationToken, ValueTask>> _initializers = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IServiceProvider? _services;
    private int _initialized;

    private SaConfigurationRuntime()
    {
    }

    /// <summary>
    /// Path of the section that holds per-client overrides, <c>sa:clients</c> by default.
    /// </summary>
    public string ClientsSectionPath { get; set; } = Clients.ClientConfiguration.DefaultSectionPath;

    /// <inheritdoc/>
    public int DynamicSourceCount
    {
        get
        {
            lock (_dynamic)
            {
                return _dynamic.Count;
            }
        }
    }

    /// <summary>
    /// Returns the runtime stored on <paramref name="builder"/>, creating it on first use.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    public static SaConfigurationRuntime GetOrAdd(IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Properties.TryGetValue(PropertyKey, out object? value)
            && value is SaConfigurationRuntime existing)
        {
            return existing;
        }

        SaConfigurationRuntime created = new();
        builder.Properties[PropertyKey] = created;
        return created;
    }

    /// <summary>
    /// Returns the runtime stored on <paramref name="builder"/>, or <see langword="null"/> when the
    /// builder never went through a Sa registration.
    /// </summary>
    public static SaConfigurationRuntime? Find(IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Properties.TryGetValue(PropertyKey, out object? value)
            ? value as SaConfigurationRuntime
            : null;
    }

    /// <summary>
    /// Attaches the service provider used by late-bound initializers.
    /// </summary>
    /// <remarks>Called by <c>AddSaConfigurationRuntime</c> the first time the service is resolved.</remarks>
    public void AttachServices(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
    }

    /// <summary>Releases the reload gate.</summary>
    public void Dispose() => _gate.Dispose();

    /// <inheritdoc/>
    public void ReloadDynamic() => ReloadDynamicAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async ValueTask ReloadDynamicAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (IConfigurationProvider provider in Snapshot(_dynamic))
                await ReloadProviderAsync(provider, cancellationToken).ConfigureAwait(false);

            // The pass reads the merged snapshot of every other provider, so it is recomputed only
            // after the dynamic ones are fresh — otherwise aliases and placeholders would be derived
            // from the previous values.
            foreach (IConfigurationProvider provider in Snapshot(_postProcessing))
                await ReloadProviderAsync(provider, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            Func<CancellationToken, ValueTask>[] initializers = Snapshot(_initializers);

            if (initializers.Length > 0 && _services is null)
            {
                throw new InvalidOperationException(
                    "The configuration uses a late-bound data source, but no service provider is attached. "
                    + "Register the runtime with services.AddSaConfigurationRuntime(builder.Configuration) "
                    + "and resolve ISaConfigurationReloader from the container, or pass the connection "
                    + "explicitly with the ConnectionString/DataSource strategy.");
            }

            foreach (Func<CancellationToken, ValueTask> initializer in initializers)
                await initializer(cancellationToken).ConfigureAwait(false);
        }

        await ReloadDynamicAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void RegisterDynamicProvider(IConfigurationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (_dynamic)
        {
            if (!_dynamic.Contains(provider))
                _dynamic.Add(provider);
        }
    }

    internal void RegisterPostProcessingProvider(IConfigurationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (_postProcessing)
        {
            if (!_postProcessing.Contains(provider))
                _postProcessing.Add(provider);
        }
    }

    internal void RegisterInitializer(Func<CancellationToken, ValueTask> initializer)
    {
        ArgumentNullException.ThrowIfNull(initializer);

        lock (_initializers)
        {
            _initializers.Add(initializer);
        }
    }

    /// <summary>
    /// Registers an initializer that receives the attached service provider.
    /// </summary>
    internal void RegisterInitializer(Func<IServiceProvider, CancellationToken, ValueTask> initializer)
    {
        ArgumentNullException.ThrowIfNull(initializer);

        RegisterInitializer(ct => initializer(
            _services ?? throw MissingServices(),
            ct));
    }

    private static InvalidOperationException MissingServices()
        => new(
            "The configuration uses a late-bound data source, but no service provider is attached. "
            + "Register the runtime with services.AddSaConfigurationRuntime(builder.Configuration).");

    private static async ValueTask ReloadProviderAsync(
        IConfigurationProvider provider,
        CancellationToken cancellationToken)
    {
        if (provider is ISaAsyncConfigurationProvider asyncProvider)
        {
            await asyncProvider.LoadAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // .NET 10 removed Reload() from the provider contract: a provider refreshed outside the
        // root raises its own token through ISaReloadableConfigurationProvider. A plain Load()
        // would update the data without notifying anyone.
        if (provider is ISaReloadableConfigurationProvider reloadable)
        {
            reloadable.Reload();
            return;
        }

        provider.Load();
    }

    private static T[] Snapshot<T>(List<T> items)
    {
        lock (items)
        {
            return [.. items];
        }
    }
}
