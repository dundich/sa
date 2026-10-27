using Sa.Outbox.PostgreSql.Configuration;

namespace Sa.Outbox.PostgreSqlTests.Configuration;

/// <summary>
/// The resolved floor is derived from <c>msg_seq</c> rows filtered by <c>tenant_id</c>, so it is
/// per (consumer group, tenant). A group-only key handed the first tenant's boundary to the rest.
/// </summary>
public class PgOutboxConsumeSettingsFloorTests
{
    [Fact]
    public void ResolvedFloor_IsScopedByTenant()
    {
        var settings = new PgOutboxConsumeSettings();

        settings.SetResolvedFloor("grp", tenantId: 1, floor: 100);
        settings.SetResolvedFloor("grp", tenantId: 2, floor: 200);

        Assert.True(settings.TryGetResolvedFloor("grp", 1, out long first));
        Assert.Equal(100, first);

        Assert.True(settings.TryGetResolvedFloor("grp", 2, out long second));
        Assert.Equal(200, second);

        Assert.False(settings.TryGetResolvedFloor("grp", 3, out _));
        Assert.False(settings.TryGetResolvedFloor("other", 1, out _));
    }
}
