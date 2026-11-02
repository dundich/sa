using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Partitional;


internal sealed class TenantProvider(IOutboxStorage storage) : ITenantProvider
{
    public ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken)
        => GetTenantIds(OutboxScope.DefaultKey, cancellationToken);

    public ValueTask<int[]> GetTenantIds(string providerKey, CancellationToken cancellationToken)
    {
        TenantSettings settings = storage.TenantSettings(providerKey);

        return settings.AutoDetect
            ? GetFromPlugService(providerKey, cancellationToken)
            : GetFromExternalSettings(settings, cancellationToken);
    }

    private async ValueTask<int[]> GetFromPlugService(string providerKey, CancellationToken cancellationToken)
    {
        IOutboxTenantDetector? detector = storage.TenantDetector(providerKey);
        if (detector == null || !detector.CanDetect) return [];

        return await detector.GetTenantIds(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int[]> GetFromExternalSettings(
        TenantSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.GetTenantIds == null) return [];

        return await settings.GetTenantIds(cancellationToken).ConfigureAwait(false);
    }
}
