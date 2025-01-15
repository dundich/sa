namespace Sa.Outbox;

/// <summary>
/// Represents the settings for partitioning in the Outbox processing system.
/// This class contains configuration options related to tenant handling and caching.
/// </summary>
public class PartitionalSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether to process messages for each tenant individually.
    /// Default is set to true, meaning messages will be processed for each tenant.
    /// </summary>
    public bool ForEachTenant { get; set; } = true;

    /// <summary>
    /// Gets or sets the duration for which tenant IDs are cached.
    /// Default is set to 2 minutes.
    /// </summary>
    public TimeSpan CacheTenantIdsDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets a function that retrieves tenant IDs asynchronously.
    /// This function takes a <see cref="CancellationToken"/> as a parameter and returns an array of tenant IDs.
    /// </summary>
    public Func<CancellationToken, Task<int[]>>? GetTenantIds { get; set; }
}