namespace Sa.Outbox;

/// <summary>
/// Represents the settings for partitioning in the Outbox processing system.
/// This class contains configuration options related to tenant handling and caching.
/// </summary>
public sealed class TenantSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether the system should automatically detect tenants
    /// by scanning incoming database messages
    /// </summary>
    public bool AutoDetect { get; set; } = false;

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
        return this;
    }

    /// <summary>
    /// Sets the interface to retrieve tenant IDs asynchronously.
    /// </summary>
    public TenantSettings WithTenantSource(ITenantSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        GetTenantIds = source.GetTenantIds;
        return this;
    }

    /// <summary>
    /// Sets static tenant IDs.
    /// </summary>
    public TenantSettings WithTenantIds(params int[] tenantIds)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        GetTenantIds = _ => ValueTask.FromResult(tenantIds);
        return this;
    }

    /// <summary>
    /// Sets auto detect mode
    /// </summary>
    public TenantSettings WithAutoDetect()
    {
        AutoDetect = true;
        return this;
    }
}
