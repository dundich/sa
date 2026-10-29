using Sa.Outbox.PostgreSql.Services;
using Sa.Outbox.PostgreSql.TypeResolve;

namespace Sa.Outbox.PostgreSqlTests.TypeResolve;

/// <summary>
/// The type map is cached in-process. These tests pin the two ways it used to go stale or lie:
/// <see cref="OutboxTypeCache.Reset"/> never invalidated, and <c>0</c> doubled as the
/// "unknown type" sentinel.
/// </summary>
public class OutboxTypeCacheTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reset_DropsTheCachedSnapshot()
    {
        var repository = new FakeTypeRepository();
        var cache = new OutboxTypeCache(repository);

        Assert.Null(await cache.GetCode("A", TestToken));

        repository.Rows.Add((1, "A"));

        // Still the snapshot taken by the first call.
        Assert.Null(await cache.GetCode("A", TestToken));

        await cache.Reset(TestToken);

        Assert.Equal(1, await cache.GetCode("A", TestToken));
    }

    [Fact]
    public async Task GetCode_StoredHashZero_IsFoundButMissingTypeIsNull()
    {
        var repository = new FakeTypeRepository();
        repository.Rows.Add((0, "Zero"));
        var cache = new OutboxTypeCache(repository);

        Assert.Equal(0, await cache.GetCode("Zero", TestToken));
        Assert.Null(await cache.GetCode("Absent", TestToken));
    }

    [Fact]
    public async Task GetCode_FailedLoad_IsRetriedAfterReset()
    {
        var repository = new FakeTypeRepository { FailOnSelect = true };
        var cache = new OutboxTypeCache(repository);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetCode("A", TestToken));

        // A faulted load used to be cached for the lifetime of the process.
        repository.FailOnSelect = false;
        repository.Rows.Add((1, "A"));

        Assert.Equal(1, await cache.GetCode("A", TestToken));
    }

    [Fact]
    public async Task GetHashCode_HashCollision_ThrowsInsteadOfReturningTheOtherTypesCode()
    {
        var repository = new FakeTypeRepository();
        var cache = new OutboxTypeCache(repository);
        var resolver = new OutboxTypeResolver(cache, repository);

        long code = await resolver.GetHashCode("B", TestToken);

        // Same hash, different name: what a Murmur3 collision looks like to the resolver.
        repository.Rows.Clear();
        repository.Rows.Add((code, "A"));
        await cache.Reset(TestToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await resolver.GetHashCode("B", TestToken));

        Assert.Contains("'B'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'A'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetHashCode_CachedType_DoesNotReloadTheTable()
    {
        var repository = new FakeTypeRepository();
        repository.Rows.Add((1, "A"));
        var cache = new OutboxTypeCache(repository);
        var resolver = new OutboxTypeResolver(cache, repository);

        Assert.Equal(1, await resolver.GetHashCode("A", TestToken));
        var loadsAfterFirst = repository.SelectAllCalls;

        // Served by the synchronous snapshot: no second SELECT, no task on the hot path.
        Assert.Equal(1, await resolver.GetHashCode("A", TestToken));
        Assert.Equal(loadsAfterFirst, repository.SelectAllCalls);
    }

    [Fact]
    public async Task GetHashCode_UnknownType_InsertsOnceAndCachesIt()
    {
        var repository = new FakeTypeRepository();
        var cache = new OutboxTypeCache(repository);
        var resolver = new OutboxTypeResolver(cache, repository);

        long code = await resolver.GetHashCode("NewType", TestToken);

        Assert.NotEqual(0, code);
        Assert.Single(repository.Rows);
        Assert.Equal("NewType", repository.Rows[0].typeName);
        Assert.Equal(1, repository.InsertCalls);

        // Cached now: no second insert.
        Assert.Equal(code, await resolver.GetHashCode("NewType", TestToken));
        Assert.Equal(1, repository.InsertCalls);
    }

    private sealed class FakeTypeRepository : IOutboxMsgTypeRepository
    {
        public List<(long id, string typeName)> Rows { get; } = [];

        public int InsertCalls { get; private set; }

        public int SelectAllCalls { get; private set; }

        public bool FailOnSelect { get; set; }

        public Task<int> Insert(long id, string typeName, CancellationToken cancellationToken)
        {
            InsertCalls++;

            // Mirrors INSERT ... ON CONFLICT DO NOTHING: an existing key is left untouched.
            if (Rows.All(r => r.id != id))
                Rows.Add((id, typeName));

            return Task.FromResult(1);
        }

        public Task<IReadOnlyCollection<(long id, string typeName)>> SelectAll(CancellationToken cancellationToken)
        {
            SelectAllCalls++;

            if (FailOnSelect)
                throw new InvalidOperationException("select failed");

            return Task.FromResult<IReadOnlyCollection<(long id, string typeName)>>([.. Rows]);
        }
    }
}
