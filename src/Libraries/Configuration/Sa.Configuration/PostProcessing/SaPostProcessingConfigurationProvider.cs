using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Sa.Configuration.PostProcessing;

/// <summary>
/// Runs the transform pipeline over the merged configuration of every other provider and stores
/// only the values the transforms produced.
/// </summary>
/// <remarks>
/// Keeping the provider's data to the derived keys is deliberate: the configuration root merges
/// providers last-in-wins, so a pass that copied the whole configuration would shadow whatever an
/// earlier pass derived, and a source registered later would silently beat an alias. Here a key
/// this pass does not touch simply falls through to the provider that owns it.
/// <para>
/// <see cref="ConfigurationProvider.Data"/> is replaced on every load rather than mutated, so <c>Reload()</c> drops a
/// key that disappeared from its source or from the alias map instead of serving it forever.
/// </para>
/// <para>
/// The "inner" configuration — every source except the passes — is built once (rebuilding it would
/// create a second provider, and a second connection pool, per pass registration) and is reloaded
/// on every pass load. A change raised by a file watcher on the inner side is observed directly, so
/// <c>reloadOnChange</c> keeps working for the derived values too.
/// </para>
/// </remarks>
internal sealed class SaPostProcessingConfigurationProvider(
    Func<IConfiguration> getInnerConfiguration,
    SaConfigurationState state) : ConfigurationProvider, ISaReloadableConfigurationProvider, IDisposable
{
    private readonly Func<IConfiguration> _getInnerConfiguration = getInnerConfiguration;
    private readonly SaConfigurationState _state = state;
    private readonly object _sync = new();

    private IConfigurationRoot? _inner;
    private IDisposable? _innerSubscription;
    private int _initialized;
    private int _busy;
    private int _disposed;

    public override void Load()
    {
        if (!Enter())
            return;

        try
        {
            IConfigurationRoot inner = EnsureInner();

            // The inner providers were loaded when this provider was built; a reload is needed
            // only from the second load on, otherwise the first pass would read the files twice.
            if (Interlocked.Exchange(ref _initialized, 1) == 1)
                inner.Reload();

            Compute(inner);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Recomputes the pass and raises the provider's token.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="ISaConfigurationReloader"/>: the root's own reload raises the root token,
    /// but a pass refreshed on its own has to announce itself, or consumers of the derived values
    /// would keep the previous snapshot.
    /// </remarks>
    public void Reload()
    {
        Load();
        OnReload();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return; _innerSubscription?.Dispose();
        _innerSubscription = null;

        (_inner as IDisposable)?.Dispose();
        _inner = null;
    }

    /// <summary>
    /// Answers for both spellings of a key, so <c>configuration["sa__pg__host"]</c> finds what the
    /// pass stored as <c>sa:pg:host</c>.
    /// </summary>
    public override bool TryGet(string key, out string? value)
        => base.TryGet(ConfigurationKey.Normalize(key), out value);

    /// <summary>
    /// The keys this pass actually produced. <see cref="ConfigurationProvider.Data"/> is protected,
    /// so this is the seam the tests use to prove unchanged keys are not copied.
    /// </summary>
    internal ICollection<string> StoredKeys => Data.Keys;

    private IConfigurationRoot EnsureInner()
    {
        if (_inner is { } existing)
            return existing;

        lock (_sync)
        {
            if (_inner is { } raced)
                return raced;

            IConfiguration configuration = _getInnerConfiguration()
                ?? throw new InvalidOperationException("The inner configuration could not be built.");

            IConfigurationRoot inner = configuration as IConfigurationRoot
                ?? throw new InvalidOperationException("The inner configuration is not a configuration root.");

            _inner = inner;

            // A changed file (reloadOnChange) reloads only its own inner provider; the pass has to
            // recompute the derived values from the new data.
            _innerSubscription = ChangeToken.OnChange(inner.GetReloadToken, OnInnerReloaded);

            return inner;
        }
    }

    private void OnInnerReloaded()
    {
        if (!Enter())
            return;

        try
        {
            Compute(EnsureInner());
            OnReload();
        }
        finally
        {
            Exit();
        }
    }

    private void Compute(IConfiguration inner)
    {
        // The merged snapshot of every other provider. Keys are canonicalized here once, which
        // makes every later lookup independent of how a source spelled a separator.
        Dictionary<string, string?> working = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string?> pair in inner.AsEnumerable())
        {
            if (pair.Value is null)
                continue;

            working[ConfigurationKey.Normalize(pair.Key)] = pair.Value;
        }

        MergedConfigurationView view = new(working);
        Dictionary<string, string?> output = new(StringComparer.OrdinalIgnoreCase);

        foreach (ISaConfigurationTransform transform in _state.BuildTransforms())
        {
            // Snapshot: writing to the view is how the next transform sees this one's output.
            foreach (KeyValuePair<string, string?> change in transform.Apply(view).ToArray())
            {
                string key = ConfigurationKey.Normalize(change.Key);
                output[key] = change.Value;
                view.Set(key, change.Value);
            }
        }

        Data = output;
    }

    private bool Enter()
    {
        if (Volatile.Read(ref _disposed) == 1)
            return false;

        return Interlocked.Exchange(ref _busy, 1) == 0;
    }

    private void Exit() => Volatile.Write(ref _busy, 0);
}
