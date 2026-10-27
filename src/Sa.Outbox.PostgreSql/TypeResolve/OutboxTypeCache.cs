using Sa.Classes;
using Sa.Outbox.PostgreSql.Services;

namespace Sa.Outbox.PostgreSql.TypeResolve;

internal sealed class OutboxTypeCache : IOutboxTypeCache
{
    private readonly IOutboxMsgTypeRepository _repository;

    private readonly ResetLazy<Task<Storage>> _cache;

    /// <summary>
    /// Token of the caller that started the current load. <see cref="ResetLazy{T}"/>'s factory
    /// takes no arguments, so the token cannot be passed in; it is stored here before
    /// <see cref="ResetLazy{T}.Value"/> starts the factory. Later callers share that load.
    /// <c>volatile</c>: the field is read by the load and written by whichever caller starts it,
    /// and a bare <see cref="CancellationToken"/> field could be read torn.
    /// </summary>
    private volatile LoadScope _loadScope = new(CancellationToken.None);

    public OutboxTypeCache(IOutboxMsgTypeRepository repository)
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

        public long? GetCode(string typeName)
            => _typeHash.TryGetValue(typeName, out var code) ? code : null;

        public string? GetType(long code)
        {
            if (_hashType.TryGetValue(code, out var name)) return name;
            return default;
        }
    }

    public async Task<long?> GetCode(string typeName, CancellationToken cancellationToken)
    {
        Storage storage = await GetStorage(cancellationToken).ConfigureAwait(false);
        return storage.GetCode(typeName);
    }

    public async Task<string?> GetTypeName(long code, CancellationToken cancellationToken)
    {
        Storage storage = await GetStorage(cancellationToken).ConfigureAwait(false);
        return storage.GetType(code);
    }

    public Task Reset(CancellationToken cancellationToken)
    {
        // Reset is not cancellable on purpose: the caller has already written the row, and
        // honouring a cancel here would leave the in-process map stale forever. The token is
        // part of the interface for symmetry with the async members.
        _ = cancellationToken;
        _cache.Reset();
        return Task.CompletedTask;
    }

    private Task<Storage> GetStorage(CancellationToken cancellationToken)
    {
        // Only the caller that starts the load owns its token. Replacing this after the factory
        // has started would not affect the in-flight request anyway — it reads the scope once.
        if (!_cache.IsValueCreated)
            _loadScope = new LoadScope(cancellationToken);

        return Observe(_cache.Value);
    }

    /// <summary>
    /// A faulted load used to stick in <see cref="ResetLazy{T}"/> forever: every later
    /// <see cref="GetCode"/> replayed the same failure and never tried the database again.
    /// </summary>
    private async Task<Storage> Observe(Task<Storage> load)
    {
        try
        {
            return await load.ConfigureAwait(false);
        }
        catch
        {
            _cache.Reset();
            throw;
        }
    }

    private async Task<Storage> Load()
    {
        CancellationToken token = _loadScope.Token;
        var hashCodes = await _repository.SelectAll(token).ConfigureAwait(false);
        return new Storage(hashCodes);
    }

    private sealed class LoadScope(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
    }
}
