namespace Sa.Outbox.Partitional;

internal sealed class PartitionalSupportCache(PartitionalSettings? settings = null) : IPartitionalSupportCache
{
    private Lazy<Task<int[]>>? _cache;


    public async ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken)
    {
        if (settings == null) return [];

        _cache ??= new Lazy<Task<int[]>>(() => ExtractTenantIds(cancellationToken));

        return await _cache.Value;
    }

    private async Task<int[]> ExtractTenantIds(CancellationToken cancellationToken)
    {
        if (settings?.GetTenantIds == null) return [];
        int[] ids = await settings.GetTenantIds(cancellationToken);
        return ids;
    }
}
