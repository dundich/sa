using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.InMemory;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Tests.InMemory;

/// <summary>
/// Shared wiring for the in-memory provider tests: a container with the provider registered, a
/// controllable clock, and the batch/filter builders the delivery pipeline would produce.
/// </summary>
internal static class InMemoryTestHost
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static ServiceProvider Build(
        Action<InMemoryOutboxSettings>? configure = null,
        TestClock? clock = null,
        string scopeKey = OutboxScope.DefaultKey)
    {
        ServiceCollection services = new();
        // TryAddSingleton inside the provider keeps this one, so the store leases against test time.
        if (clock is not null)
            services.AddSingleton<TimeProvider>(clock);

        services.AddSaOutboxUsingInMemory(configure, scopeKey: scopeKey);

        return services.BuildServiceProvider();
    }

    public static OutboxMessageBatch<T> Batch<T>(
        T[] payloads,
        int tenantId = 1,
        string part = "root",
        DateTimeOffset? createdAt = null)
    {
        OutboxMessageBatchItem<T>[] items = new OutboxMessageBatchItem<T>[payloads.Length];

        for (int i = 0; i < payloads.Length; i++)
            items[i] = new OutboxMessageBatchItem<T>($"id-{i}", payloads[i]);

        return new OutboxMessageBatch<T>(items, new OutboxPartInfo(tenantId, part, createdAt ?? Start));
    }

    public static OutboxMessageFilter Filter<T>(
        string transactId,
        int tenantId = 1,
        string part = "root",
        DateTimeOffset? now = null,
        string consumerGroup = "group")
    {
        DateTimeOffset at = now ?? Start;

        return new OutboxMessageFilter(
            TransactId: transactId,
            ConsumerGroupId: consumerGroup,
            PayloadType: typeof(T).Name,
            TenantId: tenantId,
            Part: part,
            FromDate: DateTimeOffset.MinValue,
            ToDate: at,
            NowDate: at);
    }

    public static IOutboxContextOperations<T>[] RentBuffer<T>(int size) => new IOutboxContextOperations<T>[size];

    public static InMemoryOutboxStore Store(this IServiceProvider provider, string scopeKey = OutboxScope.DefaultKey)
        => provider.GetRequiredKeyedService<InMemoryOutboxStore>(scopeKey);

    public static IOutboxBulkWriter Writer(this IServiceProvider provider, string scopeKey = OutboxScope.DefaultKey)
        => provider.GetRequiredKeyedService<IOutboxBulkWriter>(scopeKey);

    public static IOutboxDeliveryManager Manager(this IServiceProvider provider, string scopeKey = OutboxScope.DefaultKey)
        => provider.GetRequiredKeyedService<IOutboxDeliveryManager>(scopeKey);

    public static IOutboxTenantDetector Detector(this IServiceProvider provider, string scopeKey = OutboxScope.DefaultKey)
        => provider.GetRequiredKeyedService<IOutboxTenantDetector>(scopeKey);
}

/// <summary>A clock the tests move by hand.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public DateTimeOffset Now => _now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>The payload type the tests publish and rent.</summary>
internal sealed record TestMessage(string Value);
