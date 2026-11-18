# Sa.Outbox.PostgreSql

**English** | [Русский](Readme-ru.md)

An AOT-compatible library for implementing the **Transactional Outbox** pattern using PostgreSQL in .NET applications. Provides guaranteed message delivery with support for multi-tenancy, concurrent processing, partitioning, and advanced consumer management.

## Installation

```bash
dotnet add package Sa.Outbox.PostgreSql
```

## Contents

- [Installation](#installation)
- [What it actually looks like](#what-it-actually-looks-like) — the model compared with Redis Stream and Kafka
- [Quick Start](#quick-start-5-minutes)
- [Publishing inside a Caller Transaction](#publishing-inside-a-caller-transaction)
- [Setup](Setup/Readme.md)
  - [Per-group Settings from Configuration (`configurationKey`)](Setup/Readme.md#per-group-settings-from-configuration-configurationkey)
  - [Consumer group settings (`OutboxConsumerSettingsBuilder`)](Setup/Readme.md#consumer-group-settings-outboxconsumersettingsbuilder)
  - [Latency budget for every group at once](Setup/Readme.md#latency-budget-for-every-group-at-once)
  - [Runtime settings management](Setup/Readme.md#runtime-settings-management)
- [Message Lifecycle](Lifecycle/Readme.md)
- [Result Statuses](#result-statuses)
- [Database Architecture](#database-architecture)
- [Bulk publishing](Bulk/Readme.md)
- [Requirements](#requirements)
- [License](#license)

Detailed configuration (tables, migrations, cleanup, `PgPollingSettings`) lives in [`Configuration/Readme.md`](Configuration/Readme.md).

The provider's internals each have their own readme: [`Commands/Readme.md`](Commands/Readme.md) (data-access
commands and batching), [`SqlBuilder/Readme.md`](SqlBuilder/Readme.md) (the SQL templates),
[`TypeResolve/Readme.md`](TypeResolve/Readme.md) (the type ↔ hash cache).

## What it actually looks like

By its runtime model this is closer to **Redis Stream** and **Kafka** than to the classic "outbox → broker":

| | Redis Stream | Kafka | here |
|---|---|---|---|
| Log | `XADD` into a stream | append-only partition | `__msg$` + `msg_seq` (BIGSERIAL) |
| Groups | `XGROUP` / `XREADGROUP` | consumer group | `consumer_group` in the group settings |
| Cursor | last-delivered-id per group | `__consumer_offsets` | `__offset$.group_offset_seq` per `(group, tenant)` |
| Partitioning | — | partition | `(tenant, part)` → daily leaves |
| Delivery | PEL + `XACK`, claim by timeout | poll + commit offset | `task_lock_expires_on` + `msg.Ok()/Warn()/…` |
| Ordering | within a stream | within a partition | within `(tenant, part)` |
| Retention | `XTRIM` / `MAXLEN` | time or size | `DROP PARTITION` per day |

What it is *not*: the classic "put it in the outbox, a separate relay publishes it to Kafka/RabbitMQ"
pipeline. There is no broker, no CDC/Debezium and no relay process — the PostgreSQL tables themselves
are the log and the queue, and the consumer reads straight from the database. If you need a bus, this
schema can play that role: consumer groups give the same fan-out.

What a real broker has and this does not:

- no wire protocol or client libraries — the "broker" is a PostgreSQL schema;
- no dynamic rebalancing: groups are static (registered in DI), and parallelism inside a group comes
  from `SKIP LOCKED`, not from partition assignment;
- no global ordering: it exists only within `(tenant, part)`;
- no retention by offset: retention drops whole daily partitions;
- as a bus it works only inside one database — it is shared schema, not network transport.

---

## Quick Start (5 minutes)

One complete example — copy, paste, run:

```csharp
using Microsoft.Extensions.Hosting;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.Publication;
using Sa.Outbox.Serialization;

IHost host = Host.CreateDefaultBuilder()
    .ConfigureServices(services => services
        // ① Register outbox core + tenants + consumers
        .AddSaOutbox(builder => builder
            .WithTenants((_, t) => t.WithTenantIds(1, 2, 3))
            .WithDeliveries(b => b
                .AddDeliveryScoped<OrderConsumer, OrderCreated>((_, s) =>
                {
                    s
                        .WithInterval(TimeSpan.FromSeconds(5))
                        .StartImmediately()
                        .WithMaxBatchSize(16)
                        .WithMaxDeliveryAttempts(3)
                        .WithLockDuration(TimeSpan.FromSeconds(10))
                        .WithLookbackInterval(TimeSpan.FromDays(7));
                })
            )
        )
        // ② Wire up PostgreSQL
        .AddSaOutboxUsingPostgreSql(cfg => cfg
            .WithDataSource(ds => ds
                .Options(ob => ob.Configure(o => o.ConnectionString =
                    "Host=localhost;Database=outbox_db;Username=postgres;Password=postgres")))
            .WithOutboxSettings((_, settings) =>
            {
                settings.TableSettings.WithSchema("outbox");
                settings.CleanupSettings.WithRetentionPeriod(TimeSpan.FromDays(30));
            })
            .WithMessageSerializer(JsonOutboxMessageSerializer.Instance) // the core default; replace with yours
        )
    )
.Build();

// ③ Publish a message
var publisher = host.Services.GetRequiredService<IOutboxMessagePublisher>();
await publisher.Publish([
    new OrderCreated("order-42", "Premium Widget"),
    new OrderCreated("order-43", "Standard Gadget")
], tenantId: 1);

// ④ Run (consumers will pick up messages automatically)
await host.RunAsync();


// ─── Your types ────────────────────────────────────────────────

public sealed record OrderCreated(string PayloadId, string ProductName);

public sealed class OrderConsumer : IConsumer<OrderCreated>
{
    public async ValueTask Consume(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<OrderCreated>> messages,
        CancellationToken ct)
    {
        foreach (var msg in messages.Span)
        {
            Console.WriteLine($"[{filter.ConsumerGroupId}] {msg.Payload.ProductName}");
            msg.Ok();   // ← mark success
        }
    }
}
```

That's it. The library handles:
- Table creation & partitioning
- BINARY COPY bulk insertion
- Concurrent consumption via `SKIP LOCKED`
- Retry / postpone / error workflows
- Automatic cleanup of old partitions
- Self-bootstrapping of consumer settings into `IOutboxConsumerManager`

---

## Publishing inside a Caller Transaction

Normally every `Publish(...)` writes on its own connection and commits immediately (auto-commit). When the outbox write must commit **atomically** with your own database changes (the classic outbox transactionality), publish inside an `NpgsqlTransaction` you control:

```csharp
using Sa.Outbox.PostgreSql.Publication;   // PgOutboxPublisherExtensions
using Sa.Outbox.Publication;
using Sa.Data.PostgreSql;                 // IPgDataSource / PgTransaction

// Open the transaction through the base PostgreSQL data source:
IPgDataSource dataSource = services.GetRequiredService<IPgDataSource>();
await using PgTransaction sqlTx = await dataSource.BeginTransaction(ct);

// Outbox rows go into the caller's transaction:
await publisher.PublishInTransaction(events, tenantId: 1, sqlTx.Transaction, ct);

// ... your own inserts on sqlTx.Connection / sqlTx.Transaction ...

await sqlTx.CommitAsync(ct);     // outbox rows become visible atomically with your changes
// -- dispose without commit (or RollbackAsync) discards everything, outbox rows included
```

`PgOutboxPublisherExtensions` adds typed `PublishInTransaction(...)` / `PublishSingleInTransaction(...)` helpers that accept an `NpgsqlTransaction` (for a `PgTransaction`, pass `sqlTx.Transaction`; a wrapper overload would duplicate the whole matrix). The name differs from `Publish` because an instance method always shadows a same-named extension — the typed call should be explicit in the code. The binary COPY then runs on that transaction's connection, inside that transaction, and **bypasses the automatic retry layer** — retrying over a foreign or aborted transaction is invalid, so failures bubble to the caller.

Passing no context keeps the default auto-commit behavior. There is deliberately **no ambient/implicit scope** — the transaction is always an explicit argument.

Guards:

- The transaction must belong to the **same database** (host / port / database) as the outbox data source — a foreign transaction throws `InvalidOperationException` before anything is written.
- The connection must be open and the transaction must be its own active transaction; otherwise `InvalidOperationException`.

> **Partition bookkeeping** (`EnsureMsgParts`) always runs on the outbox's own connection, outside your transaction, so a rolled-back publish can leave an empty partition (or a type registry row) behind — harmless leftovers, cleaned up by the normal retention jobs.
>
> **Locking note:** while your transaction is open and has written to a partition, partition DDL from another publish on the same data source (e.g., a first message of a new tenant) can block until your transaction finishes. Commit (or roll back) before publishing more outside the transaction.

---

## Setup

Step-by-step setup — tenants, consumer registration, connection, serialization, poison handling,
table settings and manual job runs — in a separate document: [Setup/Readme.md](Setup/Readme.md).

## Message Lifecycle

How a message travels from `Publish(...)` to `Ok()` — materialization, renting, statuses and
consumer groups — in a separate document: [Lifecycle/Readme.md](Lifecycle/Readme.md).

## Result Statuses

After processing each message, call exactly one method:

| Method | When to use | Next action |
|---|---|---|
| `msg.Ok()` | Everything went fine | Status written, row is not rented again (removed with its partition) |
| `msg.Created()` | Side-effect resource created | Status written, row is not rented again |
| `msg.Accepted()` | Accepted for async processing | Status written, row is not rented again |
| `msg.NoContent()` | Processed, no data to return | Status written, row is not rented again |
| `msg.Fail(ex)` | Unrecoverable failure | Logged to `__error$`, no retry |
| `msg.Warn(ex)` | Transient issue (network, timeout) | Rented again after the batching window |
| `msg.Postpone(ts)` | Need to wait before retry | Requeued after `ts` |
| `msg.Retry(ts, reason)` | Retry with metadata | Requeued with attempt info |
| `msg.Aborted(reason)` | Intentionally skip | Marked skipped, no retry |
| `msg.ErrorMaxAttempts()` | Max attempts exhausted | Logged to `__error$`, no retry |

`Error`/`Error5xx` and `ErrorMaxAttempts` are additionally handed to `IPgOutboxPoisonHandler` before
the status is written; see [Poison Handler](Setup/Readme.md#5-poison-handler-permanent-failures).

---

## Database Architecture

```
📂 my_outbox (schema)
├── 📄 outbox__msg$      — Source messages (read-only, binary COPY)
├── 📄 outbox            — Task queue (read-write, SKIP LOCKED)
├── 📄 outbox__log$      — Delivery history (read-only)
├── 📄 outbox__error$    — Permanent errors (read-only)
├── 📄 outbox__type$     — Type ↔ hash registry (read-only)
└── 📄 outbox__offset$   — Consumer group offsets (advisory lock)
```

### Under the hood

| Mechanism | What it solves |
|---|---|
| **`msg_seq` (BIGSERIAL)** | Database-assigned insertion order, and **the** consumption cursor (`__offset$.group_offset_seq`). Denormalised into the task table so `RETURNING msg_seq` advances the cursor in the same statement |
| **UUID v7 message id** | Application-minted id, used as the join key between `__msg$` and the task queue. Deliberately **not** used for ordering: it comes from application clocks, which skew and get backdated |
| **BINARY COPY** | Maximum throughput for bulk message insertion |
| **SKIP LOCKED** | Multiple workers safely compete for tasks — and only for tasks. On the single cursor row "skip" would mean reporting an empty queue while a backlog exists |
| **Advisory Locks** | Offset coordination per `(consumer group, tenant)` |
| **Publish lock** | `pg_advisory_xact_lock` per `(tenant, part)` around the COPY — makes `msg_seq` order equal commit order, so the `max(msg_seq)` cursor cannot pass a row that is still committing |
| **Cursor index** | `(msg_seq)` on `__msg$` — turns the loader's cursor read into an index range scan instead of a Seq Scan of the whole lookback window (measured 56.8 ms → 0.166 ms on 7 × 40k rows) |
| **Daily partitions** | Pruning keeps one poll on a few leaves, and retention is a `DROP PARTITION` (indexes leave with the leaf) instead of a `DELETE` |
| **murmurHash3** | Compact type identification cached in `__type$` — see [`TypeResolve/Readme.md`](TypeResolve/Readme.md) |
| **SqlCacheSplitter** | Splits large multi-row INSERT/UPDATE statements into chunks up to the shared `DefaultMaxLen` (1024) — see [`SqlBuilder/Readme.md`](SqlBuilder/Readme.md) and [`Commands/Readme.md`](Commands/Readme.md) |

---

## Bulk publishing

Rules for high-volume publishing and what to measure in production — in a separate document:
[Bulk/Readme.md](Bulk/Readme.md).

## Migrating 0.17 → 0.18 (existing deployments)

**The database schema does not change.** The tables, columns, partitions, `__offset$` and the
`msg_seq` cursor are the same; `OutboxRentToken` lives in memory only. No data migration is needed.

### Code changes

| Before | After |
|---|---|
| `IOutboxBulkWriter`: 4 overloads, the legacy one abstract | one abstract `InsertBulk(batch, context, ct)`; the legacy overloads and `OutboxMessage<T>` are removed |
| `OutboxDeliveryMessage<T>(OutboxId, OutboxMessage<T>, DeliveryInfo)` | a flat record `(OutboxId, PayloadId, Payload, PartInfo, DeliveryInfo, RentToken)` |
| `IOutboxPlugin` (commented out) | `AddOutboxProvider(descriptor, builder)` |
| — | `IOutboxProviderRegistry`, `IOutboxPublisherResolver`, `IOutboxHealth` |

- **A custom writer** (if any): implement `InsertBulk(batch, context, ct)` and register it with
  `AddOutboxProvider(...).WithBulkWriter(sp => new MyWriter(...))`.
- **A custom consumer** keeps working: string keys and the string overloads of
  `IOutboxConsumerManager` are preserved.
- The PG/Redis packages are already on the new contracts — an application that only uses
  `AddSaOutbox`/`AddSaOutboxUsingPostgreSql` and `IOutboxMessagePublisher` needs no changes.

### Registration

- The default provider key stays `"outbox"`; its unkeyed aliases are kept:
  `IOutboxMessagePublisher`, `IOutboxProviderRegistry`, `PgOutboxSettings` (+ components),
  `PgPollingSettings`, `IDatabase` (Redis), and the three storage contracts. Existing code that
  resolves them without a key keeps working.
- New one-call overload: `AddSaOutboxUsingPostgreSql(cfg, outbox)`, with `scopeKey` written once.
- `WithOutboxSettings`/`WithPolling` actions are now per provider; one registration no longer affects
  a second provider.

### Adding a second PG provider

1. Give it **its own schema** (or its own table names):
   `WithOutboxSettings((_, s) => s.TableSettings.WithSchema("billing"))`.
2. The startup `PgMultiProviderGuard` fails when two providers point at the same tables (every
   message would be delivered twice), i.e. the same `(host, port, database)` with the same
   schema/table names.
3. The second provider's partitions are created by its own migration job (in its own schema).
4. Consumer groups may now share an id across providers: the manager and consumers are addressed by
   `(providerKey, groupId)`; the string overloads throw an ambiguity error.

### Moving an existing outbox to a non-default key

- The PG storage does not change: same schema/tables/cursors, so stored messages and offsets are read
  as before.
- The data source becomes keyed (`AddSaPostgreSqlDataSource(key, ...)`); a borrowed unkeyed
  `NpgsqlDataSource` is still found through `PgDataSourceResolver`.
- The schedule and the partitional module move to the new scope key.

### Rollback

0.18 writes no new schema, so rolling back to 0.17 is safe. The only trace is the second provider's
tables if it already created partitions; they simply stay unused.

> Redis: moving an existing provider to a non-default key changes the stream prefix
> (`sa:outbox` → `sa:outbox:{key}`). Set the prefix explicitly via `WithOutboxSettings` to keep the old
> keys.

## Requirements

- **.NET 10.0** or higher
- **PostgreSQL 15+**
- Compatible with **Native AOT**

## License

MIT
