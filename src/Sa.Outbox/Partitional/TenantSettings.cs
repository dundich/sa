namespace Sa.Outbox.Partitional;

/// <summary>
/// Represents the settings for partitioning in the Outbox processing system.
/// This class contains configuration options related to tenant handling and caching.
/// </summary>
public sealed class TenantSettings
{
    /// <summary>
    /// Tracks that an explicit tenant source (see <see cref="WithTenantIds"/>,
    /// <see cref="WithTenantSource"/> or <see cref="WithTenantProvider"/>) has been set. Used to
    /// reject <see cref="WithAutoDetect"/> once a source is authoritative, since the two modes are
    /// mutually exclusive and a later call would silently ignore the source.
    /// </summary>
    private bool _explicitSourceSet;

    /// <summary>
    /// Gets or sets a value indicating whether the system should automatically detect tenants
    /// by scanning incoming database messages
    /// </summary>
    public bool AutoDetect { get; private set; } = true;

    /// <summary>
    /// Gets or sets a function that retrieves tenant IDs asynchronously.
    /// </summary>
    public Func<CancellationToken, ValueTask<int[]>> GetTenantIds { get; private set; }
        = (_) => ValueTask.FromResult<int[]>([]);

    /// <summary>
    /// Sets the function to retrieve tenant IDs asynchronously.
    /// </summary>
    public TenantSettings WithTenantProvider(Func<CancellationToken, ValueTask<int[]>> getTenantIds)
    {
        ArgumentNullException.ThrowIfNull(getTenantIds);
        GetTenantIds = getTenantIds;
        _explicitSourceSet = true;
        // An explicit provider is authoritative: it must not fall through to plug-service
        // auto-detection (which would ignore the provider and return []).
        return WithNoAutoDetect();
    }

    /// <summary>
    /// Sets the interface to retrieve tenant IDs asynchronously.
    /// </summary>
    public TenantSettings WithTenantSource(ITenantSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        GetTenantIds = source.GetTenantIds;
        _explicitSourceSet = true;
        // An explicit source is authoritative: it must not fall through to plug-service
        // auto-detection (which would ignore the source and return []).
        return WithNoAutoDetect();
    }

    /// <summary>
    /// Sets static tenant IDs.
    /// </summary>
    public TenantSettings WithTenantIds(params int[] tenantIds)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        GetTenantIds = _ => ValueTask.FromResult(tenantIds);
        _explicitSourceSet = true;
        // An explicit static list is authoritative: it must not fall through to
        // plug-service auto-detection (which would ignore the list and return []).
        return WithNoAutoDetect();
    }

    /// <summary>
    /// Enables auto-detection mode.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when an explicit tenant source has already been set with
    /// <see cref="WithTenantIds"/>, <see cref="WithTenantSource"/> or
    /// <see cref="WithTenantProvider"/>. The two modes are mutually exclusive, so turning
    /// auto-detection on after an explicit source would silently ignore that source.
    /// </exception>
    public TenantSettings WithAutoDetect()
    {
        if (_explicitSourceSet)
        {
            throw new InvalidOperationException(
                "Auto-detection cannot be enabled after an explicit tenant source has been set " +
                "(WithTenantIds, WithTenantSource or WithTenantProvider). The two modes are " +
                "mutually exclusive — call one of them, or WithAutoDetect, but not both.");
        }

        AutoDetect = true;
        return this;
    }

    public TenantSettings WithNoAutoDetect()
    {
        AutoDetect = false;
        return this;
    }
}
