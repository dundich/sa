using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>
/// Sugar over <see cref="IPgOutboxConfiguration.WithPolling"/>: sets the polling defaults of every
/// consumer group of the PostgreSQL Outbox from a single number, from a named profile, or from an
/// <c>IConfiguration</c> section.
/// </summary>
/// <remarks>
/// These are defaults, not overrides: a group's <c>IConfiguration</c> section (the
/// <c>configurationKey</c> passed to <c>AddDelivery</c>) and its <c>configure</c> callback are
/// applied later and therefore win. Without a call to one of these methods (or to
/// <c>WithPolling</c> itself) nothing is registered and the Outbox behaves exactly as before.
/// </remarks>
public static class PgOutboxPollingExtensions
{
    /// <summary>
    /// Bounds the worst-case time from publishing a message to the start of its processing by an
    /// idle consumer group, for every group that does not override it.
    /// </summary>
    /// <remarks>
    /// The budget is split as <c>BatchingWindow (3 s) + reserve + IdleMaxInterval</c>, with the
    /// reserve at <c>max(5 s, 20% of the budget)</c>; <see cref="PgPollingSettings.Interval"/> is
    /// derived as a third of the idle bound and <c>MaxProcessingIterations</c> becomes <c>-1</c>, so a
    /// burst is drained in the same cycle. See <see cref="PgPollingSettings.UseBudget"/> for details.
    /// </remarks>
    /// <param name="configuration">The PostgreSQL Outbox configuration.</param>
    /// <param name="deliveryBudget">
    /// The allowed publication-to-processing latency, for example <c>TimeSpan.FromMinutes(1)</c>.
    /// </param>
    /// <returns>The same <see cref="IPgOutboxConfiguration"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The budget leaves no room for a poll period. Thrown here, at registration, rather than on the
    /// first group build at startup.
    /// </exception>
    public static IPgOutboxConfiguration WithPollingBudget(
        this IPgOutboxConfiguration configuration,
        TimeSpan deliveryBudget)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Fail fast at registration: the registered delegate runs later (when the settings are
        // materialised from the container), and a typo in the budget should not wait until startup.
        _ = new PgPollingSettings().UseBudget(deliveryBudget);

        return configuration.WithPolling((_, settings) => settings.UseBudget(deliveryBudget));
    }

    /// <summary>
    /// Applies one of the named polling profiles to every group that does not override it.
    /// </summary>
    /// <param name="configuration">The PostgreSQL Outbox configuration.</param>
    /// <param name="profile">The profile; <see cref="PgPollingProfile.Balanced"/> by default.</param>
    /// <returns>The same <see cref="IPgOutboxConfiguration"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined profile, thrown at registration.</exception>
    public static IPgOutboxConfiguration WithPollingProfile(
        this IPgOutboxConfiguration configuration,
        PgPollingProfile profile = PgPollingProfile.Balanced)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Fail fast at registration — same reason as WithPollingBudget.
        _ = new PgPollingSettings().UseProfile(profile);

        return configuration.WithPolling((_, settings) => settings.UseProfile(profile));
    }

    /// <summary>
    /// Binds <see cref="PgPollingOptions"/> from an <c>IConfiguration</c> section, so the latency
    /// profile can be changed without a rebuild.
    /// </summary>
    /// <remarks>
    /// The <c>IConfiguration</c> is resolved from the container when the settings are materialised;
    /// no <c>IConfiguration</c> registered, or the section missing/empty, makes this a no-op. Both are
    /// normal in a bare container and in a deployment that keeps the polling values in code.
    /// </remarks>
    /// <param name="configuration">The PostgreSQL Outbox configuration.</param>
    /// <param name="sectionPath">The section path, for example <c>Sa:Outbox:Pg:Polling</c>.</param>
    /// <returns>The same <see cref="IPgOutboxConfiguration"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sectionPath"/> is null, empty or whitespace.</exception>
    public static IPgOutboxConfiguration WithPollingFromConfiguration(
        this IPgOutboxConfiguration configuration,
        string sectionPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);

        return configuration.WithPolling((sp, settings) =>
        {
            IConfiguration? config = sp.GetService<IConfiguration>();
            if (config is null) return;

            IConfigurationSection section = config.GetSection(sectionPath);
            if (!section.Exists()) return;

            PgPollingOptions options = new();
            section.Bind(options);
            options.ApplyTo(settings);
        });
    }
}
