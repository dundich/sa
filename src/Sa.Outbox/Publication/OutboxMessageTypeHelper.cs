using Sa.Outbox.Support;
using System.Collections.Concurrent;
using System.Reflection;

namespace Sa.Outbox.Publication;

internal record OutboxMessageTypeInfo(string PartName);

internal static class OutboxMessageTypeHelper
{
    private static readonly ConcurrentDictionary<Type, OutboxMessageTypeInfo> s_cache = new();

    public static OutboxMessageTypeInfo GetOutboxMessageTypeInfo(Type type)
        => s_cache.GetOrAdd(type, GetTypeInfo);

    public static OutboxMessageTypeInfo GetOutboxMessageTypeInfo<T>()
        => s_cache.GetOrAdd(typeof(T), GetTypeInfo);

    private static OutboxMessageTypeInfo GetTypeInfo(Type typeToCheck)
    {
        return new OutboxMessageTypeInfo(PartName: GetPartValue(typeToCheck));
    }

    private static string GetPartValue(Type type)
    {
        OutboxMessageAttribute? attribute = type.GetCustomAttribute<OutboxMessageAttribute>();
        return attribute?.Part ?? OutboxMessageAttribute.Default.Part;
    }
}
