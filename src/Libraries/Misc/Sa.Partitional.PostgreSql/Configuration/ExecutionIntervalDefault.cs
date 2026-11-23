namespace Sa.Partitional.PostgreSql.Configuration;

/// <summary>
/// Default tick interval shared by the migration and cleanup schedules: four hours plus a per-instance
/// jitter of up to 59 minutes.
/// </summary>
/// <remarks>
/// The jitter exists so several instances that are started together do not tick together (thundering
/// herd). It is evaluated once per settings instance, at construction, and then behaves like a plain
/// configured value: rebinding the section or assigning <c>ExecutionInterval</c> replaces it. Kept in
/// one place so the two settings types cannot drift apart in either the base interval or the jitter
/// range (they used to say «up to 59 minutes» while rolling 1..58).
/// </remarks>
internal static class ExecutionIntervalDefault
{
    /// <summary>The fixed part of the default interval.</summary>
    public static readonly TimeSpan Base = TimeSpan.FromHours(4);

    /// <summary>
    /// Returns <see cref="Base"/> plus 0–59 minutes. Not deterministic on purpose: each settings
    /// instance gets its own jitter.
    /// </summary>
    public static TimeSpan Next() => Base.Add(TimeSpan.FromMinutes(Random.Shared.Next(0, 60)));
}
