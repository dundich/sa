using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Partitional;


internal sealed class TenantProvider(
    TenantSettings settings,
    IOutboxTenantDetector? detector = null) : ITenantProvider
{
    public ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken)
    {
        return settings.AutoDetect
            ? GetFromPlugService(cancellationToken)
            : GetFromExternalSettings(cancellationToken);
    }

    private async ValueTask<int[]> GetFromPlugService(CancellationToken cancellationToken)
    {
        if (detector == null || !detector.CanDetect) return [];
        return await detector.GetTenantIds(cancellationToken);
    }

    private async ValueTask<int[]> GetFromExternalSettings(CancellationToken cancellationToken)
    {
        if (settings?.GetTenantIds == null) return [];
        return await settings.GetTenantIds(cancellationToken);
    }
}
