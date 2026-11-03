using Sa.Outbox.Partitional;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Redis.Configuration;
using StackExchange.Redis;
using System.Globalization;

namespace Sa.Outbox.Redis.Services.Plug;

/// <summary>
/// Discovers the tenants that ever published to this Outbox.
/// </summary>
/// <remarks>
/// The publish script adds every tenant id to one set, which is cheap to read and needs no scan of
/// the stream keys. Like the PostgreSQL provider it reports only the tenants it knows about: a
/// tenant that never published has nothing to deliver, so it is indistinguishable from a missing
/// one.
/// </remarks>
internal sealed class OutboxTenantDetector(IDatabase database, RedisOutboxSettings settings) : IOutboxTenantDetector
{
    public bool CanDetect => true;

    public async ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RedisValue[] values = await database
            .SetMembersAsync(settings.KeySettings.TenantsKey())
            .ConfigureAwait(false);

        if (values.Length == 0)
            return [];

        List<int> tenants = new(values.Length);

        foreach (RedisValue value in values)
        {
            if (int.TryParse((string?)value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tenantId))
                tenants.Add(tenantId);
        }

        return [.. tenants];
    }
}
