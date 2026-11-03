using System.ComponentModel.DataAnnotations;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Represents the settings for scheduling the cleanup of Outbox message parts.
/// This class contains configuration options related to how and when parts should be cleaned up.
/// </summary>
/// <remarks>
/// Served by the standard <c>Microsoft.Extensions.Options</c> pipeline (one named instance per
/// module): section binding → code delegates → validation. Mutable POCO with scalar values only, so
/// it binds straight from <c>appsettings.json</c> and stays Native AOT friendly.
/// </remarks>
public sealed class PartCleanupScheduleSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether the cleanup should be executed as a background job.
    /// Default is set to false, meaning the cleanup will not run as a job.
    /// </summary>
    public bool AsBackgroundJob { get; set; } = false;

    /// <summary>
    /// Gets or sets the duration after which old parts will be dropped.
    /// Default is set to 30 days.
    /// </summary>
    public TimeSpan DropPartsAfterRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Gets or sets how expired parts are retired. Default is <see cref="PartCleanupMode.Drop"/> —
    /// the historical behaviour. <see cref="PartCleanupMode.Archive"/> moves them into
    /// <see cref="ArchiveSchemaName"/> instead of dropping them.
    /// </summary>
    public PartCleanupMode CleanupMode { get; set; } = PartCleanupMode.Drop;

    /// <summary>
    /// Gets or sets the schema that archived parts are moved into when
    /// <see cref="CleanupMode"/> is <see cref="PartCleanupMode.Archive"/>. Default is <c>archive</c>.
    /// </summary>
    /// <remarks>
    /// The archived table keeps its name — only the schema changes — so every module that archives
    /// into the same schema must use distinct table names (an archive schema is shared, and a name
    /// collision would fail the move). The property is ignored in <see cref="PartCleanupMode.Drop"/> mode.
    /// </remarks>
    public string ArchiveSchemaName { get; set; } = "archive";

    /// <summary>
    /// Gets or sets the interval at which the cleanup job will be executed.
    /// Default is set to every 4 hours, with a random additional delay of up to 59 minutes.
    /// </summary>
    public TimeSpan ExecutionInterval { get; set; } = TimeSpan
        .FromHours(4)
        .Add(TimeSpan.FromMinutes(Random.Shared.Next(1, 59)));

    /// <summary>
    /// Gets or sets the initial delay before the cleanup job starts executing.
    /// Default is set to 1 minute.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Validates the current settings and throws a <see cref="ValidationException"/> for the first
    /// unusable value. Called by <c>PartCleanupScheduleSettingsValidator</c> after the pipeline has
    /// bound and applied the code settings, so it validates the final values.
    /// </summary>
    /// <exception cref="ValidationException">Thrown when a value is not usable.</exception>
    /// <remarks>
    /// Explicit checks rather than <c>ValidateDataAnnotations()</c>: the latter is marked
    /// <c>RequiresUnreferencedCode</c> (IL2026) and breaks Native AOT.
    /// </remarks>
    public void Validate()
    {
        if (DropPartsAfterRetention < TimeSpan.Zero)
        {
            throw new ValidationException(
                $"PartCleanupScheduleSettings:DropPartsAfterRetention must not be negative, but was '{DropPartsAfterRetention}'.");
        }

        if (CleanupMode == PartCleanupMode.Archive)
        {
            if (string.IsNullOrWhiteSpace(ArchiveSchemaName))
            {
                throw new ValidationException(
                    $"PartCleanupScheduleSettings:ArchiveSchemaName must not be blank when CleanupMode is '{nameof(PartCleanupMode.Archive)}'.");
            }

            // The archive schema becomes an identifier in `ALTER TABLE … SET SCHEMA`, and PostgreSQL
            // truncates identifiers at 63 bytes: a longer name would send the partitions to a schema
            // that does not match the one the operator configured.
            if (System.Text.Encoding.UTF8.GetByteCount(ArchiveSchemaName) > 63)
            {
                throw new ValidationException(
                    "PartCleanupScheduleSettings:ArchiveSchemaName must not exceed PostgreSQL's 63-byte "
                    + $"identifier limit, but '{ArchiveSchemaName}' is "
                    + $"{System.Text.Encoding.UTF8.GetByteCount(ArchiveSchemaName)} bytes.");
            }
        }

        if (ExecutionInterval <= TimeSpan.Zero)
        {
            throw new ValidationException(
                $"PartCleanupScheduleSettings:ExecutionInterval must be positive, but was '{ExecutionInterval}'.");
        }

        if (InitialDelay < TimeSpan.Zero)
        {
            throw new ValidationException(
                $"PartCleanupScheduleSettings:InitialDelay must not be negative, but was '{InitialDelay}'.");
        }
    }
}
