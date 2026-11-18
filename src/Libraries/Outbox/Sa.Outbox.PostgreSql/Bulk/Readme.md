# Bulk publishing

**English** | [Русский](Readme-ru.md)

Rules for high-volume publishing and what to measure in production: COPY batches, partition fan-out, migration load and the metrics that reveal contention. Part of the [`Sa.Outbox.PostgreSql`](../Readme.md) documentation; the settings reference is in [`Configuration/Readme.md`](../Configuration/Readme.md).

## Contents

- [Rules](#rules)
- [Measurements](#measurements)
- [What to monitor in production](#what-to-monitor-in-production)

## Rules

1. **Batch 256–1024, not less.** Throughput grows with the batch (4.7k → 58k rows/s without the
   lock), because every publish pays a fixed price: BEGIN, lock, COPY, fsync on COMMIT. One publish =
   one `COPY` = one transaction.
2. **One producer per part.** The lock serializes not only the COPY but the commits of the part, so
   group commit stops amortizing fsync: 8 producers into one part give 14.8k rows/s against 55.3k
   without the lock (−67 %). With one producer per part the cost is zero; 8 parts give 43.7k rows/s
   and p50 32 ms against 23.5k/77 ms over three.
3. **Do not mix parts/tenants in one `Publish` call** — otherwise a single `COPY` takes N locks and
   holds all of them until commit.
4. **Keep transactions short.** Publishing inside a business transaction holds the part lock for its
   whole duration. Atomicity requires the same transaction context — which means the business
   transaction must be short, not "5 seconds with an external HTTP call inside".
5. **Do not add indexes to `__msg$`.** Each one costs ~140–200 B of WAL per row and 30–50 % of
   throughput. Exactly one is needed — the cursor index `(msg_seq)`. The PK is the most expensive;
   drop it only deliberately (you lose the duplicate-`msg_id` protection).
6. **Do not `COPY` directly into a leaf** — measured: not faster (72–78k against 87k), routing is not
   the bottleneck.
7. **Leave `msg_seq` alone.** The sequence costs ~4 B of WAL per row and contributes nothing to the
   ceiling. `CACHE` breaks the cursor (message loss).
8. **Server:** `wal_compression=lz4` (or `zstd`) — WAL is the biggest item, the `UNLOGGED` control
   showed up to 2.9× of headroom; `max_wal_size`/`checkpoint_timeout` so checkpoints do not run
   constantly; keep `full_page_writes` on; keep `synchronous_commit` on.
9. **Connection pooling:** `pool_mode=transaction` (not `statement` — the library works in
   transactions). The locks are transaction-scoped, so PgBouncer does not break them.
10. **Partitions:** many daily partitions do not slow inserts down, and retention via `DROP PARTITION`
    is free (indexes go away with the leaf).

## Measurements

Stand: PostgreSQL 15 in Docker, `__msg$` = `LIST(tenant) → LIST(part) → RANGE(day)`, 256 leaves,
PK `(msg_id, tenant_id, msg_part, msg_created_at)` plus the cursor index `(msg_seq)`, 200 B payload,
`synchronous_commit=on` (the real mode), fsync ≈ 11 ms. Client — Npgsql 10, `NpgsqlBinaryImporter`,
exactly as in `BulkInsertMsgCommand`. 8 producers, 8-second runs, median of 3.

The absolute numbers are a property of that stand. Carry them over with the formula at the end of the
section rather than directly; run-to-run spread on this stand is ±30 % (latencies are steadier than
throughput).

**Batch and the cost of the lock** — 8 producers, one part:

| batch | no lock, rows/s | with lock, rows/s | lock cost |
|---|---|---|---|
| 16 | 4 685 | 1 198 | −74 % |
| 64 | 17 578 | 3 524 | −80 % |
| 256 | 44 946 | 14 808 | −67 % |
| 1024 | 58 450 | 26 222 | −55 % |

**Scenarios** — batch 256, lock enabled (the "1 part" configuration is the 256 row above, measured in
a different run):

| parts | rows/s | tx/s | p50, ms | p99, ms |
|---|---|---|---|---|
| 1 | 17 183 | 67 | 99.1 | 413.4 |
| 3 | 23 498 | 92 | 77.5 | 156.3 |
| 8 | 43 683 | 171 | 32.2 | 176.0 |

**WAL per row** — batch 1024, 8 producers:

| table | WAL/row |
|---|---|
| PK + cursor index | 526 B |
| cursor index only | 386 B |
| no indexes | 319 B |
| `UNLOGGED` (control) | 3 B |

**Column legend:**

- `batch` — rows in one `COPY`, i.e. in one publish and one transaction
  (`OutboxPublishSettings.MaxBatchSize`).
- `rows/s` — rows per second, summed over all producers.
- `tx/s` — transactions (publishes) per second; with a known `batch` that is `rows/s / batch`.
- `no lock` / `with lock` — before and after the `(tenant, part)` advisory lock was introduced;
  `lock cost` is the throughput lost to it.
- `parts` — how many distinct `(tenant, part)` are in play; 8 producers over 3 parts means ~2.7
  producers per key.
- `p50` / `p99` — percentiles of one publish's latency on the client, including lock wait.
- `WAL/row` — WAL bytes per inserted row.

**Formula for carrying this over to your hardware:**

```
t_txn ≈ F + batch × t_row
rows/s per part ≈ batch / t_txn
rows/s total    ≈ min(N_parts × rows/s per part, server ceiling)
```

`F` is the effective cost of one commit in your installation (group commit included), `t_row` ≈
0.02 ms per row. Both follow from two points of the batch table: `1/R(B) = F/B + t_row`, so with
`B` = 256 and `B` = 1024 (`R` = 44 946 and 58 450) we get `F ≈ 1.8 ms` and `t_row ≈ 15 µs` — effective
values for 8 producers, not the raw disk fsync.

## What to monitor in production

- `xact_commit/s` and `rows/s` on `__msg$` — the actual cost of publishing;
- WAL bytes/row (`pg_current_wal_lsn` before/after a window) — expect ~350–550 B; growth means extra
  indexes or bloat;
- `pg_stat_activity.wait_event` — `Lock:advisory` for more than a few ms means the key is overloaded
  (too many producers per part);
- delivery lag: `max(msg_seq) − group_offset_seq` per group;
- publish p99 on the client, not only server-side time.
