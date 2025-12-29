namespace Sa.Outbox.PostgreSql;

public static class PgOutboxCleanupSettingsExtensions
{
    /// <summary>
    /// Configures cleanup to run as a background job.
    /// </summary>
    public static PgOutboxCleanupSettings RunAsJob(this PgOutboxCleanupSettings settings)
    {
        settings.AsBackgroundJob = true;
        return settings;
    }

    /// <summary>
    /// Configures cleanup to run immediately (not as a background job).
    /// </summary>
    public static PgOutboxCleanupSettings RunImmediately(this PgOutboxCleanupSettings settings)
    {
        settings.AsBackgroundJob = false;
        return settings;
    }

    /// <summary>
    /// Sets whether cleanup should run as a background job.
    /// </summary>
    public static PgOutboxCleanupSettings SetAsJob(
        this PgOutboxCleanupSettings settings,
        bool asJob)
    {
        settings.AsBackgroundJob = asJob;
        return settings;
    }

    /// <summary>
    /// Sets the retention period for dropping old parts.
    /// </summary>
    public static PgOutboxCleanupSettings WithRetentionPeriod(
        this PgOutboxCleanupSettings settings,
        TimeSpan retentionPeriod)
    {
        if (retentionPeriod <= TimeSpan.Zero)
            throw new ArgumentException("Retention period must be positive", nameof(retentionPeriod));

        settings.DropPartsAfterRetention = retentionPeriod;
        return settings;
    }

    /// <summary>
    /// Sets the retention period in days.
    /// </summary>
    public static PgOutboxCleanupSettings WithRetentionDays(
        this PgOutboxCleanupSettings settings,
        int days)
    {
        if (days <= 0)
            throw new ArgumentException("Retention days must be positive", nameof(days));

        settings.DropPartsAfterRetention = TimeSpan.FromDays(days);
        return settings;
    }

    /// <summary>
    /// Configures cleanup for testing purposes (no actual cleanup).
    /// </summary>
    public static PgOutboxCleanupSettings UseTestSettings(
        this PgOutboxCleanupSettings settings)
    {
        settings.AsBackgroundJob = false;
        settings.DropPartsAfterRetention = TimeSpan.MaxValue; // Never drop in tests
        settings.ExecutionInterval = TimeSpan.Zero;

        return settings;
    }

    /// <summary>
    /// Configures cleanup to never drop old parts (infinite retention).
    /// </summary>
    public static PgOutboxCleanupSettings KeepForever(
        this PgOutboxCleanupSettings settings)
    {
        settings.DropPartsAfterRetention = TimeSpan.MaxValue;
        return settings;
    }

    /// <summary>
    /// Configures cleanup with aggressive settings (frequent cleanup, short retention).
    /// </summary>
    public static PgOutboxCleanupSettings UseAggressiveCleanup(
        this PgOutboxCleanupSettings settings)
    {
        settings.AsBackgroundJob = true;
        settings.DropPartsAfterRetention = TimeSpan.FromDays(7); // Keep only 7 days
        settings.ExecutionInterval = TimeSpan.FromHours(1); // Clean every hour

        return settings;
    }

    /// <summary>
    /// Configures cleanup with conservative settings (less frequent, longer retention).
    /// </summary>
    public static PgOutboxCleanupSettings UseConservativeCleanup(
        this PgOutboxCleanupSettings settings)
    {
        settings.AsBackgroundJob = true;
        settings.DropPartsAfterRetention = TimeSpan.FromDays(90); // Keep 90 days
        settings.ExecutionInterval = TimeSpan.FromDays(1); // Clean daily

        return settings;
    }

    /// <summary>
    /// Configures cleanup with recommended production settings.
    /// </summary>
    public static PgOutboxCleanupSettings UseProductionSettings(
        this PgOutboxCleanupSettings settings)
    {
        settings.AsBackgroundJob = true;
        settings.DropPartsAfterRetention = TimeSpan.FromDays(30); // 30 days retention
        settings.ExecutionInterval = TimeSpan.FromHours(4)
            .Add(TimeSpan.FromMinutes(Random.Shared.Next(1, 59)));

        return settings;
    }
}
