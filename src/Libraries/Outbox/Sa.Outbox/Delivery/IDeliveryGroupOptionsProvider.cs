using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Resolves delivery-group settings for a consumer group from an external configuration source
/// and applies them to the <see cref="OutboxConsumerSettingsBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a storage-agnostic seam. <see cref="Sa.Outbox"/> ships a no-op provider, so a consumer
/// group configured without a <c>configurationKey</c> (or with the base package only) is shaped
/// purely by <see cref="OutboxDefaults"/> and the user <c>configure</c> callback — nothing implicit
/// sits in between.
/// </para>
/// <para>
/// A provider package (e.g. <c>Sa.Outbox.PostgreSql</c>) registers its own implementation that
/// binds a configuration section addressed by <c>configurationKey</c>. An empty key
/// means "no section to bind" — it is never a <em>group-level</em> configuration source. A provider
/// may still apply its own provider-wide defaults before the section (for example the PostgreSQL
/// package's polling defaults), so an empty key is not necessarily a no-op.
/// </para>
/// <para>
/// Applied before the user <c>configure</c> callback, so the callback always has the final word.
/// </para>
/// </remarks>
public interface IDeliveryGroupOptionsProvider
{
    /// <summary>
    /// Applies the group's configuration settings to <paramref name="builder"/>: the provider-wide
    /// defaults first, then the section addressed by <paramref name="configurationKey"/> (when one
    /// is given).
    /// </summary>
    /// <param name="consumerGroupId">The (sanitized) consumer group id.</param>
    /// <param name="configurationKey">
    /// The configuration section path for this group. When null or empty no section is bound, but
    /// provider-wide defaults may still be applied.
    /// </param>
    /// <param name="builder">The settings builder to apply non-null values to.</param>
    /// <returns>
    /// <see langword="true"/> when at least one value was applied (a provider default or a section
    /// value); <see langword="false"/> when nothing was applied.
    /// </returns>
    bool TryApply(string consumerGroupId, string? configurationKey, OutboxConsumerSettingsBuilder builder);
}

/// <summary>
/// The base no-op provider. Registered by <see cref="Sa.Outbox"/>'s delivery setup; a provider
/// package registers its own implementation to replace it for its scope.
/// </summary>
public sealed class NoopDeliveryGroupOptionsProvider : IDeliveryGroupOptionsProvider
{
    /// <inheritdoc />
    public bool TryApply(string consumerGroupId, string? configurationKey, OutboxConsumerSettingsBuilder builder)
        => false;
}

internal static class DeliveryGroupOptionsProviderSetup
{
    public static IServiceCollection AddDefaultDeliveryGroupOptionsProvider(this IServiceCollection services)
    {
        services.TryAddSingleton<IDeliveryGroupOptionsProvider, NoopDeliveryGroupOptionsProvider>();
        return services;
    }
}
