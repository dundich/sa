namespace Sa.Outbox.PostgreSql.Configuration;

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
    /// Configures migration for testing purposes: the job stays disabled, so nothing runs on its own.
    /// </summary>
    /// <remarks>
    /// The values still have to be <i>valid</i>: <c>MigrationScheduleSettings.Validate()</c> runs at
    /// host start for every named instance regardless of <see cref="PgOutboxMigrationSettings.AsBackgroundJob"/>,
    /// and it rejects <c>ForwardDays = 0</c> (an empty date window creates no partition at all) and a
    /// non-positive <c>ExecutionInterval</c>. This used to set both to zero and therefore failed
    /// validation on startup.
    /// </remarks>
    public static PgOutboxMigrationSettings UseTestSettings(
        this PgOutboxMigrationSettings settings)
    {
        settings.AsBackgroundJob = false;
        settings.ForwardDays = 1; // The current day — the smallest window the validator accepts.
        settings.ExecutionInterval = TimeSpan.FromHours(1); // Never used while the job is disabled.

        return settings;
    }
}
