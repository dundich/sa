using Sa.Outbox.Support;
using System.Collections.Concurrent;

namespace Sa.Outbox.Publication;

internal record OutboxMessageTypeInfo(string PartName);

internal static class OutboxMessageTypeHelper
{
    private static readonly ConcurrentDictionary<Type, OutboxMessageTypeInfo> s_cache = new();

    public static OutboxMessageTypeInfo GetOutboxMessageTypeInfo<T>() where T : IOutboxPayloadMessage
        => s_cache.GetOrAdd(typeof(T), _ => new OutboxMessageTypeInfo(PartName: T.PartName));

    public static OutboxMessageTypeInfo GetOutboxMessageTypeInfo(Type mt)
        => s_cache.GetValueOrDefault(mt, new OutboxMessageTypeInfo("root"));
}
