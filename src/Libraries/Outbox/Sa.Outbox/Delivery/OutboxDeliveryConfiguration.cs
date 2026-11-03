namespace Sa.Outbox.Delivery;

/// <summary>
/// Convention for the <c>IConfiguration</c> section a consumer group's settings are bound from
/// when the group was registered without an explicit <c>configurationKey</c>.
/// </summary>
/// <remarks>
/// <para>
/// A group's section is addressed as <c>{SectionPrefix}:{consumerGroupId}</c>, with
/// <see cref="DefaultSectionPrefix"/> (<c>Outbox:DeliveryGroups</c>) unless the application changes
/// it through <c>IOutboxBuilder.WithDeliveryConfigurationSection</c>. This removes the repetition of
/// <c>configurationKey: "DeliveryGroups:orders"</c> in every <c>AddDelivery</c> call and keeps the key
/// and the group identity in step — the group id is already pinned as the registration parameter.
/// </para>
/// <para>
/// An explicit <c>configurationKey</c> still wins over the convention, and an explicit empty string
/// opts the group out (no section is bound for it). Passing <see langword="null"/> as the prefix
/// disables the convention for the whole application.
/// </para>
/// <para>
/// The convention is a <em>lookup</em>, not a requirement: a missing section is a no-op, so enabling
/// it cannot change a deployment that does not have such sections in its configuration.
/// </para>
/// </remarks>
public sealed class OutboxDeliveryConfiguration
{
    /// <summary>The default section prefix: <c>Outbox:DeliveryGroups</c>.</summary>
    public const string DefaultSectionPrefix = "Outbox:DeliveryGroups";

    /// <summary>
    /// The prefix the group sections live under, or <see langword="null"/> when the convention is
    /// disabled. An empty/whitespace prefix means "disabled" and is normalized to <see langword="null"/>.
    /// </summary>
    public string? SectionPrefix { get; }

    /// <summary>Creates the convention holder. The parameterless form uses <see cref="DefaultSectionPrefix"/>.</summary>
    /// <param name="sectionPrefix">The section prefix; <see langword="null"/>, empty or whitespace disables the convention.</param>
    public OutboxDeliveryConfiguration(string? sectionPrefix = DefaultSectionPrefix)
    {
        SectionPrefix = string.IsNullOrWhiteSpace(sectionPrefix) ? null : sectionPrefix.Trim().TrimEnd(':');
    }

    /// <summary>
    /// Resolves the section path for a consumer group, or <see langword="null"/> when the convention
    /// is disabled.
    /// </summary>
    /// <param name="consumerGroupId">The (already sanitized) consumer group id.</param>
    /// <returns><c>{SectionPrefix}:{consumerGroupId}</c>, or <see langword="null"/>.</returns>
    public string? ResolveSection(string consumerGroupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroupId);

        return SectionPrefix is null ? null : $"{SectionPrefix}:{consumerGroupId}";
    }
}
