namespace Sa.Outbox;

/// <summary>
/// Represents the settings for partitioning in the Outbox processing system.
/// This class contains configuration options related to tenant handling and caching.
/// </summary>
public sealed class PartitionalSettings
{
    /// <summary>
    /// Gets or sets a function that retrieves tenant IDs asynchronously.
    /// This function takes a <see cref="CancellationToken"/> as a parameter and returns an array of tenant IDs.
    /// </summary>
    public Func<CancellationToken, ValueTask<int[]>> GetTenantIds { get; internal set; } =
        _ => ValueTask.FromResult(PartitionalSettingsExtensions.s_DefaultTenantIds);

}


/// <summary>
/// Extension methods for fluent configuration of <see cref="PartitionalSettings"/>.
/// </summary>
public static class PartitionalSettingsExtensions
{
    internal static readonly int[] s_DefaultTenantIds = [0];

    /// <summary>
    /// Sets the function to retrieve tenant IDs asynchronously.
    /// </summary>
    public static PartitionalSettings WithTenantIdProvider(
        this PartitionalSettings settings,
        Func<CancellationToken, ValueTask<int[]>> getTenantIds)
    {
        ArgumentNullException.ThrowIfNull(getTenantIds);
        settings.GetTenantIds = getTenantIds;
        return settings;
    }

    /// <summary>
    /// Sets static tenant IDs.
    /// </summary>
    public static PartitionalSettings WithTenantIds(
        this PartitionalSettings settings,
        params int[] tenantIds)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        settings.GetTenantIds = _ => ValueTask.FromResult(tenantIds);
        return settings;
    }

    /// <summary>
    /// Sets static tenant IDs.
    /// </summary>
    public static PartitionalSettings WithoutTenant(this PartitionalSettings settings)
    {
        settings.GetTenantIds = _ => ValueTask.FromResult(s_DefaultTenantIds);
        return settings;
    }
}