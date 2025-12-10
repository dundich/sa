namespace Sa.Outbox;

/// <summary>
/// Represents the settings for partitioning in the Outbox processing system.
/// This class contains configuration options related to tenant handling and caching.
/// </summary>
public sealed class PartitionalSettings
{
    internal static readonly int[] s_DefaultTenantIds = [0];

    /// <summary>
    /// Gets or sets a function that retrieves tenant IDs asynchronously.
    /// This function takes a <see cref="CancellationToken"/> as a parameter and returns an array of tenant IDs.
    /// </summary>
    public Func<CancellationToken, ValueTask<int[]>> GetTenantIds { get; private set; } = 
        _ => ValueTask.FromResult(s_DefaultTenantIds);


    public PartitionalSettings WithGetTenantIds(Func<CancellationToken, ValueTask<int[]>> getTenantIds)
    {
        GetTenantIds = getTenantIds;
        return this;
    }

    public PartitionalSettings WithTenantIds(params int[] tenantIds)
    {
        GetTenantIds = (ct) => ValueTask.FromResult(tenantIds);
        return this;
    }
}