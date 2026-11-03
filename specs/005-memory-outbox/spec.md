# SPEC-005: Memory Outbox — in-memory provider as a reference implementation

- Status: Implemented
- Owner: TBD
- Related issues/ADRs: ADR-0001 (SDD), ADR-0003 (why the storage implementation lives in the core package)
- Last updated: 2026-11-03

## Context / problem

`Sa.Outbox` defines a storage-agnostic plug point: three contracts (`IOutboxBulkWriter`,
`IOutboxDeliveryManager`, `IOutboxTenantDetector`) and one registration call (`AddOutboxProvider`).
Both existing providers require external infrastructure — `Sa.Outbox.PostgreSql` (6 852 lines, PostgreSQL +
partitions + Testcontainers) and `Sa.Outbox.Redis` (2 840 lines, Redis Streams + consumer groups).

Two gaps follow:

1. **Nothing runs without infrastructure.** Every end-to-end outbox test in the repo starts a container
   (`Sa.Outbox.PostgreSqlTests`: 28 of 46 fact-bearing files; `Sa.Outbox.RedisTests`: 7 of 15). There is no way
   to exercise publish → rent → consume → acknowledge without Docker.
2. **There is no reference implementation of the plug point.** The two real providers are dominated by
   configuration plumbing, key/DDL modelling and multi-provider guards; the storage logic itself is ~40 lines
   each. A newcomer reading them cannot see the contract — the example is buried under 3 000 lines of
   provider-specific machinery.

`Sa.HybridFileStorage` already ships an in-memory implementation (`InMemoryFileStorage`,
`AddSaInMemoryFileStorage()`) as a first-class, non-production artifact. `Sa.Outbox` has no equivalent.

## Goals

- Provide an in-memory outbox provider inside the `Sa.Outbox` package that implements the full plug-point
  contract honestly, so the pipeline can be exercised without any external service.
- Make it the executable reference for embedding a provider: the whole implementation stays in the
  ~250–350 line range, readable in one sitting.
- Give the repository its first Docker-free end-to-end outbox test suite.
- Ship a runnable sample that demonstrates wiring a provider and swapping it for PostgreSQL.

## Non-goals

- Durability of any kind: no file, no database, no snapshot. A process restart loses every message, including
  `Pending` and unfinished `Processing` ones.
- Cross-process or cross-node delivery. One provider instance serves one process.
- Production use. The provider is explicitly documented as dev/test only.
- `PublishContext` (transaction enlistment) — there is no transaction to enlist into.
- Persisted poison/error records, retention, trimming, partitioning, migrations, health probes, warm-up.
- A separate NuGet package (`Sa.Outbox.InMemory`) or a new project in `src/Sa.slnx`.
- A configurable serializer, keyed multi-provider layout guards, or `IOptions`/`IConfiguration` binding.

## Actors and scenarios

### Scenario: publish and deliver without infrastructure

**Given** an application registered with `AddSaOutboxUsingInMemory(...)` and one consumer group
**When** it publishes a message and the delivery job runs
**Then** the consumer receives the message, the delivery status is persisted in memory, and no container,
connection string or schema exists anywhere in the setup.

### Scenario: provider swap

**Given** the `InMemoryOutbox.ConsoleApp` sample
**When** `AddSaOutboxUsingInMemory(...)` is replaced by `AddSaOutboxUsingPostgreSql(...)`
**Then** the application code (publishers, `IConsumer<T>` implementations, metadata) is unchanged; only the
registration line and the serializer differ.

### Scenario: transport failure and retry

**Given** a consumer that answers `Retry`, `Postpone` or `Warn`
**When** the delivery job runs again before the delay elapses
**Then** the message is not rented; after the delay it is rented again, with the attempt counter advanced for
`Retry`/`Warn` and unchanged for `Postpone`.

### Scenario: lease lost

**Given** a rented message whose lease expired without `ReturnDelivery`
**When** the delivery job runs again
**Then** the message is rented by the new call with a new rent generation, and the old `TransactId` can no
longer extend or finish it.

### Scenario: capacity exceeded

**Given** `InMemoryOutboxSettings.Capacity` set to N
**When** a publish would exceed N stored messages
**Then** `InsertBulk` throws `OutboxException` and the batch is not partially stored.

### Scenario: restart

**Given** a process that published messages and stopped
**When** a new process starts with the same configuration
**Then** the outbox is empty. This is expected behaviour and is asserted by a test, not an accident.

## Functional requirements

- **R-1** The provider lives in `src/Libraries/Outbox/Sa.Outbox/InMemory/`, namespace `Sa.Outbox.InMemory`,
  as part of the `Sa.Outbox` project. No new project, no new package reference.
- **R-2** Public entry point:
  `AddSaOutboxUsingInMemory(this IServiceCollection services, Action<InMemoryOutboxSettings>? configure = null,
  Action<IOutboxBuilder>? configureOutbox = null, string scopeKey = "outbox")`, mirroring the shape of
  `AddSaOutboxUsingRedis`. It validates `scopeKey` with `OutboxScope.EnsureValid`, honours
  `InMemoryOutboxSettings.Enabled`, registers `TimeProvider.System` via `TryAddSingleton`, calls `AddSaOutbox`,
  then `AddOutboxProvider`.
- **R-3** Descriptor: `new OutboxProviderDescriptor("InMemory", TenantDetection | LockExtension | Postponement)`.
  `PublishContext` is not declared; `InsertBulk` throws when a non-null `context` is passed.
- **R-4** `InMemoryOutboxBulkWriter` stores serialized payloads: `IOutboxMessageSerializer` +
  `OutboxPayloadCodec` produce a self-contained `byte[]`, exactly as the real providers do. Payload type
  identity is `typeof(TMessage).Name`, matching the filter the pipeline builds.
- **R-5** Messages are grouped by `(TenantId, Part, PayloadType)` and delivered FIFO inside a group. No global
  ordering is promised.
- **R-6** `RentDelivery` returns only rentable messages: `Pending`, expired `Processing`, and `Postpone`/`Retry`/
  `Warn` whose delay elapsed; it honours `OutboxMessageFilter` (`TenantId`, `Part`, `PayloadType`,
  `ConsumerGroupId`, `FromDate`, `ToDate`, `TransactId`) and the `BatchingWindow` visibility delay
  (`ToDate = now - BatchingWindow`). It fills the caller's buffer through `IOutboxContextFactory.Create`
  and returns the number of slots written.
- **R-7** Leases are per-rental: `OutboxRentToken.Key` is the internal message id, `OutboxRentToken.SubKey` is
  the rent generation. `ExtendDelivery` extends only leases owned by `filter.TransactId` that have not expired
  and returns the number of leases actually extended; `0` means the lease is lost.
- **R-8** `ReturnDelivery` persists the consumer's `DeliveryStatus`, clears the lease, and advances the attempt
  counter for `Retry` and `Warn` (not for `Postpone`). Delay before the next rent comes from `PostponeDelay`.
- **R-9** `InMemoryOutboxTenantDetector` returns the distinct tenants that have rentable messages;
  `CanDetect` is `true`.
- **R-10** `InMemoryOutboxSettings` exposes `Enabled` (default `true`) and `Capacity` (default `0` = unlimited).
  When `Capacity > 0` and a batch would exceed it, `InsertBulk` throws `OutboxException` and stores nothing
  from that batch.
- **R-11** The store is a singleton keyed by provider key, thread-safe, and uses `TimeProvider` for every
  timestamp (never `DateTimeOffset.UtcNow`).
- **R-12** Documentation: `Sa.Outbox/Readme.md` (provider table, "differences at a glance", Provider
  Requirements), root `README.md`, and the statement in `PlugServices/Readme.md` that `Sa.Outbox` does not know
  where messages live — amended to name the in-memory provider as the deliberate exception.

## Non-functional requirements

- **Compatibility**: `net10.0`; no change to existing public types; no new package references; package ID and
  TFM unchanged; `Sa.Outbox` version 0.18.0 (unified release, no bump).
- **Performance**: publish/rent must be O(1) per message on the index and O(k) for k rented messages; no
  per-call full scans of the whole store. Not a benchmarked component.
- **Reliability**: at-least-once inside one process; a lost lease re-rents the message; concurrent publish and
  rent are safe.
- **Security/privacy**: no external I/O, no secrets, no logging of payloads.
- **Observability**: no provider-specific metrics; the core's statuses and errors remain the only signal.
  No `IOutboxProviderHealth` (a missing check is reported healthy) and no `IOutboxProviderInitializer`
  (nothing to warm up).

## Constraints and dependencies

- The core has no ADO.NET or storage dependency; the provider must not add one.
- Startup validation is core-owned: declaring `TenantDetection` requires a registered detector; consumer groups
  require a registered delivery manager; the provider key must equal the `AddSaOutbox` scope key.
- `TimeProvider` is not registered by the core; the provider registers it.
- The provider cannot live in `src/Libraries/Sa`: `Sa.Outbox` links `Sa` sources via `<Compile Include>`, so a
  reference in the other direction would be a cycle.
- `Sa.Outbox` is already in `build-sh/tasks.sh` `PROJECTS`, so packaging needs no change; `src/Sa.slnx` needs no
  change because no project is added.

## Acceptance criteria

- **AC-1** `AddSaOutboxUsingInMemory()` registers the provider; `IOutboxProviderRegistry` resolves it under the
  default and a custom scope key, with descriptor name `InMemory`.
- **AC-2** Publishing `N` messages and running the delivery job delivers all `N` in FIFO order per group.
- **AC-3** A `Retry` answer re-rents the message after `PostponeDelay` with `Attempt` advanced; `Postpone` does
  not advance `Attempt`.
- **AC-4** An expired `Processing` lease is re-rented with a new generation; the previous `TransactId` cannot
  extend it and `ExtendDelivery` returns `0`.
- **AC-5** `ExtendDelivery` returns the number of leases it actually extended for the owning `TransactId`.
- **AC-6** A non-null `context` passed to `InsertBulk` throws, and `PublishContext` is absent from the
  descriptor.
- **AC-7** `Capacity` overflow throws `OutboxException` and leaves the store unchanged.
- **AC-8** `InMemoryOutboxTenantDetector` returns exactly the tenants holding rentable messages.
- **AC-9** After a fresh provider instance (simulated restart) the store is empty; no message survives.
- **AC-10** The full publish → rent → consume → acknowledge pipeline runs in `Sa.Outbox.Tests` with no container
  and no fixture; the suite stays green.
- **AC-11** `InMemoryOutbox.ConsoleApp` runs to completion with no external service and prints the delivered
  message flow.
- **AC-12** Build is 0 warnings / 0 errors, `Sa.Outbox` packages as 0.18.0, and no other package changes.

## Open questions

None. Resolved during the design interview:

| Question | Decision |
|---|---|
| Location | `Sa.Outbox` core, `InMemory/` folder |
| Purpose | Reference implementation + dev/test provider, not production |
| Packaging | Ships inside the existing `Sa.Outbox` package, minor bump |
| Durability | Process memory only, documented and tested |
| `PublishContext` | Not declared, non-null context throws |
| Payload | Serialized bytes via `IOutboxMessageSerializer` |
| Tenant detection | Implemented |
| Capabilities | `TenantDetection | LockExtension | Postponement` |
| Lease semantics | At-least-once, expired leases re-rent |
| Capacity | Optional, overflow throws `OutboxException` |
| Tests | `Sa.Outbox.Tests/InMemory/`, no new project |
| Sample | `src/Samples/InMemoryOutbox.ConsoleApp` |
| Docs | Readmes + ADR-0003 |

## Change log

- 2026-11-03: Initial spec after a design interview; decisions recorded above.
- 2026-11-03: Implemented T2–T12: provider, tests (15 facts, Docker-free), sample, docs, ADR-0003.
  `Sa.Outbox.Tests` 214/214 green, full solution build green, sample runs end-to-end.
- 2026-11-03: T13 verified — PG 275/275, Redis 52/52, `Sa.Outbox` packages as 0.18.0, no other
  version changed.
