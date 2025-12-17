using Sa.Classes;
using Sa.Outbox.PostgreSql.Repository;

namespace Sa.Outbox.PostgreSql.TypeHashResolve;

internal sealed class MsgTypeCache : IMsgTypeCache
{
    private readonly IMsgTypeRepository _repository;

    private readonly ResetLazy<Task<Storage>> _cache;

    public MsgTypeCache(IMsgTypeRepository repository)
    {
        _repository = repository;
        _cache = new(Load);
    }

    internal class Storage
    {
        private readonly Dictionary<long, string> _hashType = [];
        private readonly Dictionary<string, long> _typeHash = [];

        internal Storage(IReadOnlyCollection<(long id, string typeName)> hashCodes)
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

    public async Task<long> GetCode(string typeName, CancellationToken cancellationToken)
    {
        Storage storage = await GetStorage();
        return storage.GetCode(typeName);
    }

    public async Task<string?> GetTypeName(long code, CancellationToken cancellationToken)
    {
        Storage storage = await GetStorage();
        return storage.GetType(code);
    }

    public Task Reset(CancellationToken cancellationToken) => Task.FromResult(() => _cache.Reset());

    private Task<Storage> GetStorage() => _cache.Value;

    private async Task<Storage> Load()
    {
        var hashCodes = await _repository.SelectAll(default);
        return new Storage(hashCodes);
    }
}
