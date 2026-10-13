# UseDefaultPartition — debugging notes (integration test)

Target: `src/Tests/Sa.Outbox.PostgreSqlTests/UseDefaultPartitionTests.cs`
Goal: prove the opt-in `PgOutboxTableSettings.UseDefaultPartition` flag — a direct
SQL INSERT of a never-migrated key must not fail and must land in the catch-all
`DEFAULT` partition.

Final state: **all 6 tests pass** (4 positive + negative-control + clean-room baseline).

## Part 1 — empty parts list (tenant resolution)

### Symptom
With `UseDefaultPartition=true`, only the root partition + `outbox__msg$__def$` were
created after migrate — no `__1`, `__1__def$`, `__1__root`, `__1__root__def$`.
Positive tests `KnownTenant_UnknownPart` / `KnownKey_OutOfRangeDate` failed with
`42P01: relation "…__def$" does not exist`.

### Diagnosis
`DeliverySnapshot.Parts` resolved correctly (`["root"]`), and the single
`TenantSettings` singleton was configured with `WithTenantIds(1)`. But
`IOutboxPartitionalSupport.GetTenantIds()` (and `TenantProvider.GetTenantIds()`)
returned `[]`.

### Root cause
`TenantProvider.GetTenantIds()` branches on the `AutoDetect` flag:

```csharp
settings.AutoDetect
    ? GetFromPlugService(ct)      // auto-detect via plug service
    : GetFromExternalSettings(ct); // call the static-list delegate
```

`TenantSettings.WithTenantIds(...)` set the `GetTenantIds` delegate but **did not**
flip `AutoDetect` to `false` — it stayed at the default `true`. So `TenantProvider`
took the `GetFromPlugService` branch, found no detector in the container, and
returned `[]`.

Paradox explained: calling `ts.GetTenantIds()` **directly** returns `[1]` (the
delegate), but `TenantProvider` never invokes that delegate while `AutoDetect = true`.

Chain during migration:

```
MsgMigrationSupport.GetParts()
  → IOutboxPartitionalSupport.GetMsgParts()
  → GetPairs(snapshot.Parts = ["root"], ct)
  → GetTenantIds() → tenantProvider.GetTenantIds() = []      ← empty here
  → if (tenantIds.Length == 0) return [];                     ← parts dropped
  → GetParts() = []                                           ← migration sees 0 parts
```

### Fix
`TenantSettings.WithTenantIds(...)` now ends with `WithNoAutoDetect()`, so a static
tenant list is authoritative and does not fall through to auto-detection. `WithTenantSource`
and `WithTenantProvider` were fixed the same way — they set the delegate but previously left
`AutoDetect` untouched, so the same silent-empty branch applied to them too.

### Result
`MsgMigrationSupport.GetParts()` resolves `[[1, "root"]]` → migration builds the full
hierarchy (root, `__1`, `__1__root`, and every `__def$`). All 4 positive tests pass.

## Part 2 — negative control SQLSTATE

### Symptom
`NoDefaultPartitionTests.WithoutFlag_UnknownTenantInsert_IsRejected_AndNoDefaultPartitionsExist`
expected SQLSTATE `3F003` ("no partition of relation") but got `23514`.

### Investigation
- `23514` is `check_violation`, not `not_null_violation` (`23502`).
- Constraints on `outbox__msg$__part$`: only `PRIMARY KEY (id)` — no CHECK on
  `msg_part`. So the `23514` is **not** a partition/CHECK violation on the payload.
- The error **message** is exactly: `no partition of relation "outbox__msg$" found for row`
  — the canonical "row routed to no partition" message. The SQLSTATE code is anomalous.
- `PgDataSource` has no error rewriting, so Postgres itself emits `23514` here.

### Proof (clean-room baseline)
`CleanRoom_PartitionNotFoundSqlState` creates a minimal partitioned table
(`PARTITION BY LIST (tenant_id)`) with **one** partition (`FOR VALUES IN (1)`) and no
default, then inserts an un-routable row (`tenant_id = 999`). Result:

```
SQLSTATE=23514
MSG=23514: no partition of relation "cr_xxxxxxxx" found for row
```

Identical behaviour to the outbox table. So the `23514` code comes from **PostgreSQL
itself** in this container (PG 15.18), not from the outbox setup. The numeric code for
this scenario is environment/build-dependent; the semantic message is stable.

### Fix
The test now asserts on the **semantic message** (`no partition of relation`) rather
than a hardcoded SQLSTATE. The invariant still holds: with the flag off, an unmigrated
key is rejected because there is no catch-all partition — unlike the flag-on case,
where the same key lands silently in a default.
