using Sa.Outbox.PostgreSql.Services;

namespace Sa.Outbox.PostgreSqlTests.Services;

/// <summary>
/// Pins the advisory-lock keys: the publish lock must be stable across processes, scoped per
/// <c>(tenant, part)</c>, and live in a different lock space than the cursor lock.
/// </summary>
public class PgAdvisoryKeysTests
{
    [Fact]
    public void ForPart_IsStableForTheSameTarget()
    {
        Assert.Equal(
            PgAdvisoryKeys.ForPart(1, "root_1"),
            PgAdvisoryKeys.ForPart(1, "root_1"));
    }

    [Fact]
    public void ForPart_DiffersByTenantAndByPart()
    {
        int baseline = PgAdvisoryKeys.ForPart(1, "root_1");

        Assert.NotEqual(baseline, PgAdvisoryKeys.ForPart(2, "root_1"));
        Assert.NotEqual(baseline, PgAdvisoryKeys.ForPart(1, "root_2"));
    }

    [Fact]
    public void Fnv_IsTheSameFunctionTheCursorLockAlreadyUsed()
    {
        // The loader used to hash inline. Extracting the loop must not silently re-key every
        // existing deployment's cursor lock (harmless, but it would be an unexplained change).
        var group = new OutboxTaskLoader.ConsumerGroupIdentifier("g1", 1);

        Assert.Equal(PgAdvisoryKeys.Fnv(group.ToString()), OutboxTaskLoader.CalculateLockKey(group));
    }

    [Fact]
    public void PublishNamespace_CannotCollideWithTheCursorLockSpace()
    {
        // The cursor takes pg_advisory_xact_lock(bigint), whose tag is (high, low, 0, 0) — for the
        // 32-bit keys the loader passes that is (0, key), or (-1, key) for a negative one. The
        // publisher's first key must therefore be neither 0 nor -1.
        Assert.NotEqual(0, PgAdvisoryKeys.PublishPartNamespace);
        Assert.NotEqual(-1, PgAdvisoryKeys.PublishPartNamespace);
    }
}
