namespace Sa.Outbox.PostgreSql.Services;

/// <summary>
/// Advisory-lock keys of the outbox, and the hash they are derived from.
/// </summary>
/// <remarks>
/// Two different locks use two different PostgreSQL forms on purpose:
/// <list type="bullet">
/// <item>the delivery cursor takes <c>pg_advisory_xact_lock(bigint)</c>, whose tag is
/// <c>(high, low, 0, 0)</c> — for the 32-bit keys the loader passes that is <c>(0, key)</c> or
/// <c>(-1, key)</c>;</item>
/// <item>the publisher takes <c>pg_advisory_xact_lock(int, int)</c> with
/// <see cref="PublishPartNamespace"/> as the first key, i.e. <c>(0x5341, key)</c>.</item>
/// </list>
/// The first tag field can therefore never be equal, so a publish lock cannot be mistaken for an
/// offset lock. Both are <em>transaction</em>-scoped: they are released by the server on COMMIT, which
/// is what keeps them safe under PgBouncer transaction pooling (session-level advisory locks are not).
/// </remarks>
internal static class PgAdvisoryKeys
{
    /// <summary>
    /// First key of the publisher's two-int lock form ("SA"). Must not be 0 or -1, which are what the
    /// single-int form of the cursor lock degenerates to.
    /// </summary>
    internal const int PublishPartNamespace = 0x5341;

    /// <summary>
    /// Lock key of one publish target. Publishes to the same <paramref name="tenantId"/> and
    /// <paramref name="part"/> are serialized by it, which is what makes <c>msg_seq</c> order equal to
    /// commit order inside that part — and therefore what makes the consumer's
    /// <c>max(msg_seq)</c> cursor safe.
    /// </summary>
    internal static int ForPart(int tenantId, string part) => Fnv($"part:{tenantId}:{part}");

    /// <summary>
    /// 32-bit FNV-1a over UTF-16 code units. Not a security hash — only a stable, cheap mapping of a
    /// name to a lock key. Two different names may collide; a collision merely serializes two
    /// unrelated publishers, it cannot lose data.
    /// </summary>
    internal static int Fnv(string value)
    {
        uint hash = 0xffffffff;
        foreach (char c in value)
        {
            hash ^= c;
            for (int i = 0; i < 8; i++)
            {
                hash = hash >> 1 ^ ((hash & 1) != 0 ? 0xA6BCD5B9u : 0);
            }
        }
        return (int)(hash ^ 0xffffffff);
    }
}
