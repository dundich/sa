using System.ComponentModel.DataAnnotations;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Settings for the automated background job that pre-creates future partitions.
/// </summary>
/// <remarks>
/// Served by the standard <c>Microsoft.Extensions.Options</c> pipeline (one named instance per
/// module): section binding → code delegates → validation. Mutable POCO with scalar values only, so
/// it binds straight from <c>appsettings.json</c> and stays Native AOT friendly.
/// </remarks>
public sealed class MigrationScheduleSettings
{
    /// <summary>
    /// Gets or sets how many days into the future to pre-create partitions on each run.
    /// Default is 2 days.
    /// </summary>
    public int ForwardDays { get; set; } = 2;

    /// <summary>
    /// Gets or sets whether the migration should run as a hosted background service (<c>true</c>)
    /// or only be triggered manually via <see cref="IMigrationService"/>.
    /// Default is <c>false</c>.
    /// </summary>
    public bool AsBackgroundJob { get; set; } = false;

    /// <summary>
    /// Gets or sets the optional name of the hosted background service job.
    /// When <c>null</c>, a default name is used.
    /// </summary>
    public string? MigrationJobName { get; set; }

    /// <summary>
    /// Gets or sets the interval between consecutive migration runs.
    /// Default is 4 hours plus a random jitter of up to 59 minutes to avoid thundering-herd effects.
    /// </summary>
    public TimeSpan ExecutionInterval { get; set; } = TimeSpan
        .FromHours(4)
        .Add(TimeSpan.FromMinutes(Random.Shared.Next(1, 59)));

    /// <summary>
    /// Gets or sets the maximum time to wait for the migration lock before the run is reported as
    /// "not completed" (result <c>-1</c>). The next tick tries again.
    /// Default is 3 seconds.
    /// </summary>
    public TimeSpan WaitMigrationTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets or sets the deadline of a single migration run, counted from the moment the run
    /// obtained the migration lock.
    /// </summary>
    /// <remarks>
    /// The run is driven by its own token, not by the caller's: a started run is not interrupted by
    /// the host shutting down, because the generated DDL is idempotent and finishing is cheaper than
    /// restarting. Keep this value below the host shutdown timeout
    /// (<c>HostOptions.ShutdownTimeout</c>, 30 s by default) so the run always ends before the
    /// scheduler gives up draining it.
    /// Default is 10 seconds.
    /// </remarks>
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Validates the current settings and throws a <see cref="ValidationException"/> for the first
    /// unusable value. Called by <c>MigrationScheduleSettingsValidator</c> after the pipeline has
    /// bound and applied the code settings, so it validates the final values.
    /// </summary>
    /// <exception cref="ValidationException">Thrown when a value is not usable.</exception>
    /// <remarks>
    /// Explicit checks rather than <c>ValidateDataAnnotations()</c>: the latter is marked
    /// <c>RequiresUnreferencedCode</c> (IL2026) and breaks Native AOT.
    /// </remarks>
    public void Validate()
    {
        // ForwardDays == 0 would run Enumerable.Range(0, 0) — an empty set, so no partition would
        // ever be created. At least the current day has to be in the window.
        if (ForwardDays < 1)
        {
            throw new ValidationException(
                $"MigrationScheduleSettings:ForwardDays must be at least 1, but was '{ForwardDays}'.");
        }

        if (ExecutionInterval <= TimeSpan.Zero)
        {
            throw new ValidationException(
                $"MigrationScheduleSettings:ExecutionInterval must be positive, but was '{ExecutionInterval}'.");
        }

        if (WaitMigrationTimeout < TimeSpan.Zero)
        {
            throw new ValidationException(
                $"MigrationScheduleSettings:WaitMigrationTimeout must not be negative, but was '{WaitMigrationTimeout}'.");
        }

        if (RunTimeout <= TimeSpan.Zero)
        {
            throw new ValidationException(
                $"MigrationScheduleSettings:RunTimeout must be positive, but was '{RunTimeout}'.");
        }

        if (MigrationJobName is { } jobName && string.IsNullOrWhiteSpace(jobName))
        {
            throw new ValidationException(
                "MigrationScheduleSettings:MigrationJobName must not be blank when it is set.");
        }
    }
}
