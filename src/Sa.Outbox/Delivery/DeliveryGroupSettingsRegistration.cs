using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Per-group registration descriptor handed from <c>AddDeliveryJob</c> to the
/// <see cref="OutboxConsumerManager"/> through DI.
/// <para>
/// Settings are registered in two stages:
/// <list type="number">
/// <item>
/// <b>Registration time</b> — <see cref="RegistrationTimeSettings"/> is built eagerly from
/// <see cref="OutboxDefaults"/> and the user <c>configure</c> callback (called with a
/// <c>null</c> provider, as before). The configuration section cannot be consulted yet —
/// <see cref="IConfiguration"/> requires a live provider. The manager is pre-populated with this
/// snapshot, so the group is known (and the built settings are captured) as soon as the DI
/// container is configured — no scheduler needed.
/// </item>
/// <item>
/// <b>Startup</b> — <see cref="BuildFull"/> rebuilds the full chain (<see cref="OutboxDefaults"/>
/// → configuration section, when a key is given → user <c>configure</c> with a live provider)
/// and the delivery job factory swaps the snapshot with this result via
/// <see cref="IOutboxConsumerManager.Apply(string, Func{OutboxConsumerSettings, OutboxConsumerSettings})"/>,
/// so the configuration section lands in the actually-scheduled job.
/// </item>
/// </list>
/// The user <c>configure</c> callback therefore runs twice (once per stage); it must be an
/// idempotent builder configuration, as it always was.
/// </para>
/// </summary>
internal sealed class DeliveryGroupSettingsRegistration(
    string consumerGroupId,
    OutboxConsumerSettings registrationTimeSettings,
    Func<IServiceProvider, OutboxConsumerSettings> buildFull)
{
    /// <summary>
    /// The consumer group identity — the <c>consumerGroupId</c> registration parameter,
    /// pinned in both stages (see <c>AddDeliveryJob</c>). The user <c>configure</c>
    /// callback cannot rename it: identity, the consumer DI key, the settings-registry key and
    /// the job name all use this value, so they always match.
    /// </summary>
    public string ConsumerGroupId { get; } = consumerGroupId;

    /// <summary>The stage-1 snapshot, built eagerly at registration time.</summary>
    public OutboxConsumerSettings RegistrationTimeSettings { get; } = registrationTimeSettings;

    /// <summary>Builds the stage-2 snapshot — the full chain, with a live provider.</summary>
    public OutboxConsumerSettings BuildFull(IServiceProvider sp) => buildFull(sp);
}
