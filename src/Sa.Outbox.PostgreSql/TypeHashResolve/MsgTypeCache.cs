using Sa.Outbox.PostgreSql.Repository;
using ZiggyCreatures.Caching.Fusion;

namespace Sa.Outbox.PostgreSql.TypeHashResolve;

internal sealed class MsgTypeCache(
    IFusionCacheProvider cacheProvider
    , IMsgTypeRepository repository
    , PgOutboxCacheSettings cacheSettings)
    : IDisposable, IMsgTypeCache
{
    internal static class Env
    {
        public const string CacheName = "sa-msgtype";
    }

    private readonly IFusionCache _cache = cacheProvider.GetCache(Env.CacheName);

    internal class Storage
    {
        private readonly Dictionary<long, string> _hashType = [];
        private readonly Dictionary<string, long> _typeHash = [];

        internal Storage(List<(long id, string typeName)> hashCodes)
        {
            foreach (var (id, typeName) in hashCodes)
            {
                _hashType[id] = typeName;
                _typeHash[typeName] = id;
            }
        }

        public long GetCode(string typeName)
        {
            if (_typeHash.TryGetValue(typeName, out var code)) return code;
            return 0;
        }

        public string? GetType(long code)
        {
            if (_hashType.TryGetValue(code, out var name)) return name;
            return default;
        }
    }

    public async ValueTask<long> GetCode(string typeName, CancellationToken cancellationToken)
    {
        var storage = await GetStorage(cancellationToken);
        return storage.GetCode(typeName);
    }

    public async ValueTask<string?> GetTypeName(long code, CancellationToken cancellationToken)
    {
        var storage = await GetStorage(cancellationToken);
        return storage.GetType(code);
    }

    public ValueTask Reset(CancellationToken cancellationToken) => _cache.RemoveAsync(Env.CacheName, token: cancellationToken);

    private ValueTask<Storage> GetStorage(CancellationToken cancellationToken)
    {
        return _cache.GetOrSetAsync<Storage>(
            Env.CacheName
            , async (context, t) => await Load(context, t)
            , options: null
            , token: cancellationToken);
    }

    private async Task<Storage> Load(FusionCacheFactoryExecutionContext<Storage> context, CancellationToken cancellationToken)
    {
        List<(long id, string typeName)> hashCodes = await repository.SelectAll(cancellationToken);
        context.Options.Duration = hashCodes.Count > 0 ? cacheSettings.CacheTypeDuration : TimeSpan.Zero;
        return new Storage(hashCodes);
    }

    public void Dispose() => _cache.Dispose();
}
