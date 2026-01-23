namespace Sa.Outbox.PostgreSql;

public static class PgOutboxMigrationSettingsExtensions
{
    /// <summary>
    /// Configures migration to run as a background job.
    /// </summary>
    /// <param name="settings">The migration settings.</param>
    /// <returns>The configured settings instance.</returns>
    public static PgOutboxMigrationSettings RunAsJob(this PgOutboxMigrationSettings settings)
    {
        settings.AsBackgroundJob = true;
        return settings;
    }


    /// <summary>
    /// Sets the execution interval for the migration job.
    /// </summary>
    /// <param name="settings">The migration settings.</param>
    /// <param name="interval">The execution interval.</param>
    /// <returns>The configured settings instance.</returns>
    public static PgOutboxMigrationSettings WithExecutionInterval(
        this PgOutboxMigrationSettings settings,
        TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentException("Execution interval must be positive", nameof(interval));

        settings.ExecutionInterval = interval;
        return settings;
    }

    /// <summary>
    /// Configures migration with recommended production settings.
    /// </summary>
    public static PgOutboxMigrationSettings UseProductionSettings(
        this PgOutboxMigrationSettings settings)
    {
        settings.AsBackgroundJob = true;
        settings.ForwardDays = 7; // Longer forward window for production
        settings.ExecutionInterval = TimeSpan.FromHours(6)
            .Add(TimeSpan.FromMinutes(Random.Shared.Next(1, 59)));

        return settings;
    }

    /// <summary>
    /// Configures migration with recommended development settings.
    /// </summary>
    public static PgOutboxMigrationSettings UseDevelopmentSettings(
        this PgOutboxMigrationSettings settings)
    {
        settings.AsBackgroundJob = true;
        settings.ForwardDays = 1;
        settings.ExecutionInterval = TimeSpan.FromHours(1);

        return settings;
    }

    /// <summary>
    /// Configures migration for testing purposes.
    /// </summary>
    public static PgOutboxMigrationSettings UseTestSettings(
        this PgOutboxMigrationSettings settings)
    {
        settings.AsBackgroundJob = false;
        settings.ForwardDays = 0; // No forward movement in tests
        settings.ExecutionInterval = TimeSpan.Zero; // No interval for tests

        return settings;
    }
}
