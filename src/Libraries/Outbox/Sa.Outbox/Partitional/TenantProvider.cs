using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Partitional;


internal sealed class TenantProvider(IOutboxStorage storage) : ITenantProvider
{
    public ValueTask<int[]> GetTenantIdsAsync(CancellationToken cancellationToken)
        => GetTenantIdsAsync(OutboxScope.DefaultKey, cancellationToken);

    public ValueTask<int[]> GetTenantIdsAsync(string providerKey, CancellationToken cancellationToken)
    {
        TenantSettings settings = storage.TenantSettings(providerKey);

        return settings.AutoDetect
            ? GetFromPlugServiceAsync(providerKey, cancellationToken)
            : GetFromExternalSettingsAsync(settings, cancellationToken);
    }

    private async ValueTask<int[]> GetFromPlugServiceAsync(string providerKey, CancellationToken cancellationToken)
    {
        IOutboxTenantDetector? detector = storage.TenantDetector(providerKey);
        if (detector == null || !detector.CanDetect) return [];

        return await detector.GetTenantIdsAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int[]> GetFromExternalSettingsAsync(
        TenantSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.GetTenantIds == null) return [];

        return await settings.GetTenantIds(cancellationToken).ConfigureAwait(false);
    }
}
