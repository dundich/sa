namespace Sa.Outbox;

/// <summary>
/// The dependency-injection scope of the Outbox: the key under which it registers its internal
/// <c>Sa.Schedule</c> schedule and its <c>Sa.Partitional.PostgreSql</c> module.
/// </summary>
/// <remarks>
/// The Outbox never uses the default (unkeyed) schedule or partitional module. Everything it
/// schedules — the partitional migration and cleanup jobs and every delivery job — lives in one
/// named schedule under this key, and the PostgreSQL partitional module is registered under the
/// same key. That keeps an application's own <c>AddSaSchedule()</c> / <c>AddSaPartitional()</c>
/// registrations completely separate from the Outbox's.
/// <para>
/// Resolve the Outbox scheduler with
/// <c>sp.GetRequiredKeyedService&lt;Sa.Schedule.IScheduler&gt;(OutboxScope.DefaultKey)</c>.
/// </para>
/// </remarks>
public static class OutboxScope
{
    /// <summary>
    /// The default scope key used by <c>AddSaOutbox</c> / <c>AddSaOutboxUsingPostgreSql</c> when
    /// the caller does not supply its own.
    /// </summary>
    public const string DefaultKey = "outbox";

    /// <summary>
    /// Validates a caller-supplied Outbox scope key and returns it.
    /// </summary>
    /// <param name="scopeKey">The key to validate. A <c>null</c> or blank value, or the reserved
    /// <see cref="Sa.Schedule.Setup.DefaultName"/> (<c>"default"</c>), is rejected: the Outbox
    /// must never share the application's default schedule / partitional module.</param>
    /// <returns>The validated key.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scopeKey"/> is null, empty,
    /// whitespace, or <see cref="Sa.Schedule.Setup.DefaultName"/>.</exception>
    public static string EnsureValid(string? scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);

        if (scopeKey == Sa.Schedule.Setup.DefaultName)
        {
            throw new ArgumentException(
                $"The key \"{Sa.Schedule.Setup.DefaultName}\" is reserved for the application's " +
                "default schedule / partitional module. The Outbox needs its own key so it never " +
                "shares them; pick a different value or omit the parameter to use " +
                $"\"{DefaultKey}\".",
                nameof(scopeKey));
        }

        return scopeKey;
    }
}
