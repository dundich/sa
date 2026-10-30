namespace Sa.Partitional.PostgreSql.Migration;

/// <summary>
/// Constants used by the migration background job (identifier and default name).
/// </summary>
public static class MigrationJobConstance
{
    /// <summary>
    /// The unique <see cref="Guid"/> assigned to the built-in migration background job.
    /// </summary>
    public readonly static Guid MigrationJobId = Guid.Parse("43588353-0005-4C84-97CA-40F2A620BC4C");

    /// <summary>
    /// The default display name for the migration background job when no custom name is provided.
    /// </summary>
    public const string MigrationDefaultJobName = "Migration job";
}
