namespace Sa.Outbox.Partitional;

internal interface ITenantProvider : ITenantSource
{
    /// <summary>
    /// Tenant ids of one provider: its own settings/detector, with the shared fallback.
    /// </summary>
    /// <param name="providerKey">The provider key (outbox scope key).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    ValueTask<int[]> GetTenantIdsAsync(string providerKey, CancellationToken cancellationToken);
}
