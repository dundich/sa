# Plan — SPEC-005: Memory Outbox

## Scope and assumptions

- One new folder inside an existing project: `src/Libraries/Outbox/Sa.Outbox/InMemory/`. No new `.csproj`, no
  `Sa.slnx` change, no `build-sh/tasks.sh` change (`Sa.Outbox` is already in `PROJECTS`).
- The provider implements only the three plug-point contracts. Batching, the delivery loop, status
  interpretation, back-off, default serialization and startup validation stay core-owned.
- Version bump: `Sa.Outbox` 0.18.0 → 0.19.0 (new public API surface).
- Tests go into the existing Docker-free `Sa.Outbox.Tests` (`InternalsVisibleTo` already present).
- One new sample project, one ADR, doc edits.

## Affected projects/files

| Path/project | Change | Reason | Risk |
|---|---|---|---|
| `src/Libraries/Outbox/Sa.Outbox/InMemory/*.cs` (new) | store + 3 contract impls + settings + setup | the feature | medium |
| `src/Libraries/Outbox/Sa.Outbox/Sa.Outbox.csproj` | `0.18.0` → `0.19.0` | new public API | low |
| `src/Libraries/Outbox/Sa.Outbox/Readme.md`, `Readme-ru.md` | provider table, differences matrix, requirements | discoverability | low |
| `src/Libraries/Outbox/Sa.Outbox/PlugServices/Readme.md` | amend "does not know where messages live" | consistency | low |
| `README.md`, `README-ru.md` | provider list | consistency | low |
| `src/Tests/Sa.Outbox.Tests/InMemory/*.cs` (new) | ~15 facts, no container | verification | low |
| `src/Samples/InMemoryOutbox.ConsoleApp/` (new) | runnable demo, no Docker | the "example" half | low |
| `src/Sa.slnx` | one `/Samples/` entry | sample must build | low |
| `docs/decisions/ADR-0003-*.md` (new) | why the storage impl lives in core | record the trade-off | low |
| `specs/005-memory-outbox/*` | spec/plan/tasks | process | none |

## Design

### State

```csharp
sealed class InMemoryOutboxRecord            // mutable, guarded by the store lock
{
    long Id;                                 // monotonic per store, also OutboxRentToken.Key
    string TenantId, Part, PayloadType, PayloadId;
    byte[] Payload;
    DateTimeOffset CreatedAt;                // TimeProvider
    DeliveryStatusCode Status;
    int Attempt;
    DateTimeOffset? AvailableAt;             // Postpone/Retry/Warn delay
    DateTimeOffset? LeaseExpiresAt;
    long RentGeneration;                     // OutboxRentToken.SubKey
    string? TransactId;                      // owner of the current lease
    string? StatusMessage;
}
```

Store (`InMemoryOutboxStore`, singleton, keyed by provider key):

- `object _gate` + `Dictionary<long, InMemoryOutboxRecord> _byId`;
- `Dictionary<GroupKey, SortedSet<long>> _index` where `GroupKey = (TenantId, Part, PayloadType)`, ordered by
  `Id` — FIFO per group, O(1) insert, O(k) rent for k messages.
- `Capacity` checked against `_byId.Count` before a batch is committed, so a rejected batch stores nothing.
- Every timestamp comes from `TimeProvider.GetUtcNow()`; `DateTimeOffset.UtcNow` is never used.

### Contracts

`InMemoryOutboxBulkWriter : IOutboxBulkWriter`
- Serializes each batch item through `IOutboxMessageSerializer` + `OutboxPayloadCodec` into `byte[]`.
- Assigns `Id`, `CreatedAt`, `Status = Pending`; indexes by `(tenant, part, typeof(TMessage).Name)`.
- Returns the number stored; throws `OutboxException` when `context != null` or `Capacity` would be exceeded.

`InMemoryOutboxDeliveryManager : IOutboxDeliveryManager`
- `RentDelivery`: walks the group's ordered ids for the filter's tenant/part/payload type, selects records that
  are `Pending`, expired `Processing`, or delayed records whose `AvailableAt` passed; also applies
  `FromDate`/`ToDate` (`ToDate` already carries the `BatchingWindow` visibility delay). Marks each rented record
  `Processing`, sets `LeaseExpiresAt = now + lockDuration`, increments `RentGeneration`, stores `TransactId`,
  builds contexts via `IOutboxContextFactory.Create<TMessage>(deliveryMessage)` into the caller's buffer.
- `ExtendDelivery`: extends only records owned by `filter.TransactId` whose lease is still valid; returns the
  number actually extended (`0` = lease lost).
- `ReturnDelivery`: writes the consumer's status and message, clears the lease, advances `Attempt` for
  `Retry`/`Warn` (not `Postpone`), and sets `AvailableAt = now + PostponeDelay` for delayed outcomes.

`InMemoryOutboxTenantDetector : IOutboxTenantDetector`
- `CanDetect => true`; `GetTenantIds` returns distinct tenants holding rentable records.

`InMemoryOutboxSettings`
- `Enabled` (default `true`), `Capacity` (default `0` = unlimited).

`Setup.AddSaOutboxUsingInMemory(...)`
- `OutboxScope.EnsureValid(scopeKey)` → short-circuit when `!Enabled` → `TryAddSingleton(TimeProvider.System)` →
  keyed singleton store + writer + manager + detector → `AddSaOutbox(configureOutbox, scopeKey)` →
  `AddOutboxProvider(scopeKey, descriptor, builder)`.

### Deliberate omissions

No `IOutboxProviderHealth`, no `IOutboxProviderInitializer`, no poison persistence, no retention, no
`PublishContext`, no multi-provider guard, no `IOptions` binding. Each is listed in the spec's non-goals and in
the readme's limitations section.

## Compatibility and migration

- No existing public type changes; additive API only. `Sa.Outbox` 0.18.0 → 0.19.0.
- Nothing to migrate: the provider has no external storage, so switching to it starts from an empty outbox.
- No AOT impact: no reflection, no dynamic code; serialization stays behind `IOutboxMessageSerializer`.

## Test strategy

- **Unit (no Docker)** in `Sa.Outbox.Tests/InMemory/`:
  - registration/DI: default and custom scope key, descriptor name and capability flags, disabled short-circuit,
    missing-detector guard not triggered;
  - publish: FIFO order per group, tenant/part/type isolation, `context` rejection, capacity overflow;
  - delivery: rent → return → status persisted, `Retry` vs `Postpone` attempt accounting, delayed availability,
    expired-lease re-rent with a new generation, `ExtendDelivery` count and lost-lease `0`;
  - detector: exact tenant set;
  - restart: a second provider instance sees an empty store.
- **Sample**: `InMemoryOutbox.ConsoleApp` publishes, consumes and prints; run manually as the smoke check.
- **AOT/trimming/analyzers**: `dotnet build src/Sa.slnx -c Release` must stay 0 warnings / 0 errors.
- **Packaging/API**: `Sa.Outbox` packages as 0.19.0; no other package changes.

## Rollout and observability

- One PR: code + tests + sample + docs + ADR. No feature flag; the provider is opt-in by registration.
- Observability is whatever the core already emits (statuses, error log). No new metrics.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Provider drifts from the real contract (e.g. dishonest `ExtendDelivery`) | Tests mirror the PG/Redis suites: re-rent, lost lease, attempt accounting |
| Someone ships it to production | Readme limitation block, sample comment, ADR-0003; no health probe pretending durability |
| Store grows without bound in a long dev session | `Capacity` setting + documented behaviour |
| Core invariant "does not know where messages live" erodes | ADR-0003 states it is a single, deliberate exception |
| Sample adds build weight to the solution | ~80 lines, no Docker, no new packages |

## Rollback

Delete `InMemory/`, the test folder and the sample; revert the version bump and doc edits. No data migration, no
published API removed (0.19.0 can be superseded by 0.19.1 if needed).

## Decisions required

None outstanding — all recorded in the spec's decision table.
