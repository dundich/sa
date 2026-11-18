# Message Lifecycle

**English** | [Русский](Readme-ru.md)

How a message travels from `Publish(...)` to `Ok()`: materialization into the group queues, renting, statuses and consumer groups. Part of the [`Sa.Outbox.PostgreSql`](../Readme.md) documentation.

## Contents

- [Consumer groups](#consumer-groups)

```
Publish(messages)
      │  COPY (BINARY) — one transaction + advisory lock (tenant, part)
      ▼
  outbox__msg$          ← the message lives here, msg_seq = nextval()
      │
      │ ① materialize — once per consumer group
      │    INSERT INTO outbox … SELECT … WHERE msg_seq > @offset
      │    under the (consumer_group, tenant) advisory lock;
      │    __offset$ is advanced in the same transaction
      ▼
    outbox  (task queue)   ← one row per "message × group" pair
      │
      │ ② rent — SELECT … FOR UPDATE SKIP LOCKED
      │    → delivery_status_code = Processing, task_lock_expires_on
      ▼
  IConsumer<T>.Consume
      │
      │ ③ finish — ReturnDelivery
      │    __log$: one record per delivery attempt
      │    __error$: terminal failures only
      │    outbox: result status + attempt counter
      ▼
  the row stays in outbox with its status and is not rented again
  (Pending/Processing/Postpone/Retry/Warn stay rentable);
  it is physically removed only with its daily partition, on retention
```

Note: `__offset$` is a *materialisation* watermark, not a *delivery* watermark. It moves when the
tasks are created, before the consumer has seen them. That is safe: once it has moved, the tasks are
already in `outbox`, so a crash between materialisation and delivery loses nothing — the tasks are
simply rented later.

## Consumer groups

Every group is an independent copy of the stream. A message in `__msg$` is materialised **once per
group** that subscribes to its type (part + payload type), so groups neither block nor consume each
other's messages:

```
                         outbox__msg$   (shared: LIST(tenant) → LIST(part) → RANGE(day))
                               │
             ┌─────────────────┼─────────────────┐   one copy per group
             ▼                 ▼                 ▼
      ┌─────────────┐   ┌─────────────┐   ┌─────────────┐
      │  "orders"   │   │   "audit"   │   │  "billing"  │
      │ cursor      │   │ cursor      │   │ cursor      │
      │ (grp,tenant)│   │ (grp,tenant)│   │ (grp,tenant)│
      └──────┬──────┘   └──────┬──────┘   └──────┬──────┘
             ▼                 ▼                 ▼
       task queue        task queue        task queue        ← per-group partition
             │                 │                 │
      IConsumer<Order>  IConsumer<Order>  IConsumer<Invoice>  ← per-group handler
             │                 │                 │
             ▼                 ▼                 ▼
      __log$ (grp)      __log$ (grp)      __log$ (grp)        ← per-group delivery log
```

`"orders"` and `"audit"` above are two groups on the **same** message type: both receive every
message, each into its own queue. `"billing"` subscribes to a different type and only gets its own.

What is per group and what is shared:

| Resource | Where | Granularity |
|---|---|---|
| Cursor | `__offset$` | `(consumer_group, tenant)` |
| Task queue | `outbox` | `LIST(tenant) → LIST(consumer_group) → RANGE(day)` |
| Delivery log | `__log$` | `LIST(tenant) → LIST(consumer_group) → RANGE(day)` |
| Schedule and settings (`Interval`, `MaxBatchSize`, `MaxProcessingIterations`, `LockDuration`, …) | the `DeliveryJob` in the scheduler | per group |
| Materialisation lock | advisory lock | `(consumer_group, tenant)` — groups never block each other |
| Messages | `__msg$` | shared |
| Type registry | `__type$` | shared |
| Terminal errors | `__error$` | shared, not split by group (partitioned by day) |

Practical consequences:

- **consumption capacity is per group**, not per database: `MaxBatchSize × MaxProcessingIterations`
  per `Interval` (with the defaults 128 × 10 per minute ≈ 1 280 messages/min per group);
- to consume faster, add groups (different types/parts) or raise the batch and poll frequency — but not
  the number of workers on one group, which is bounded by that group's own lock;
- each group has its own cursor, so a lagging group does not slow the others down;
- a message whose type no group subscribes to stays in `__msg$` and leaves only with its partition on
  retention.
