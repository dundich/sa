using System.ComponentModel.DataAnnotations;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Settings that control the partition cache window and how long a loaded snapshot is reused.
/// </summary>
/// <remarks>
/// Served by the standard <c>Microsoft.Extensions.Options</c> pipeline (one named instance per
/// module): section binding → code delegates → validation. Mutable POCO with scalar values only, so
/// it binds straight from <c>appsettings.json</c> and stays Native AOT friendly.
/// </remarks>
public sealed class PartCacheSettings
{
    /// <summary>
    /// Gets or sets the look-back window applied when reloading partition metadata. A reload keeps
    /// every partition whose range starts at or after <c>now - CachedFromDate</c> and drops those
    /// that ended earlier, so the snapshot runs from that instant onwards.
    /// Default is 1 day, which is enough to still include the partition currently being written to.
    /// </summary>
    public TimeSpan CachedFromDate { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets or sets how long a loaded snapshot stays usable before the next read re-queries the
    /// database. The deadline is checked on the read path, so nothing is refreshed in the
    /// background and the cache holds no timer.
    /// Default is 5 minutes.
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Validates the current settings and throws a <see cref="ValidationException"/> for the first
    /// unusable value. Called by <c>PartCacheSettingsValidator</c> after the pipeline has bound and
    /// applied the code settings, so it validates the final values.
    /// </summary>
    /// <exception cref="ValidationException">Thrown when a value is not usable.</exception>
    /// <remarks>
    /// Explicit checks rather than <c>ValidateDataAnnotations()</c>: the latter is marked
    /// <c>RequiresUnreferencedCode</c> (IL2026) and breaks Native AOT.
    /// </remarks>
    public void Validate()
    {
        if (CacheTtl <= TimeSpan.Zero)
        {
            throw new ValidationException(
                $"PartCacheSettings:CacheTtl must be positive, but was '{CacheTtl}'.");
        }

        if (CachedFromDate < TimeSpan.Zero)
        {
            throw new ValidationException(
                $"PartCacheSettings:CachedFromDate must not be negative, but was '{CachedFromDate}'.");
        }
    }
}
