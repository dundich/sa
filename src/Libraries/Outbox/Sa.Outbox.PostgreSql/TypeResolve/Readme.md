# Type Resolve — Sa.Outbox.PostgreSql

## Purpose

The **Type Resolve** module provides a lightweight, cache-aware mechanism for mapping between fully qualified type names and compact integer codes (`long`) within the outbox system.

In the outbox architecture, every message type (e.g., `"OrderCreated"`, `"EmailSent"`) is stored in the `__type$` table as a `(long id, string typeName)` pair. Instead of storing the full type name string in every message record, the system stores only the numeric code — reducing storage footprint and improving join performance on partitioned tables.

## Components

| Class / Interface | Role |
|---|---|
| `IOutboxTypeCache` | Read-only cache layer over the `__type$` table. Loads all type mappings into memory on demand. |
| `OutboxTypeCache` | Concrete implementation. Holds bidirectional dictionaries (`long → string` and `string → long`) refreshed lazily via `ResetLazy`. |
| `IOutboxTypeResolver` | Higher-level resolver that coordinates cache hits, DB inserts, and hash computation. |
| `OutboxTypeResolver` | Orchestrates: check cache → compute MurmurHash3 fallback → insert into DB → refresh cache. Thread-safe via `Interlocked`. |
| `Setup.AddOutboxTypeResolver()` | DI extension that registers both services as singletons. Called automatically during `Sa.Outbox.PostgreSql.Setup`. |

## How it works

1. **Lookup by name** (`GetHashCode(typeName)`):
   - Check in-memory cache → if found, return code.
   - If not found, compute `MurmurHash3` of the type name → use as code.
   - Insert `(code, typeName)` into `__type$` table (once per process, guarded by `Interlocked.CompareExchange`).
   - Reset cache so subsequent lookups hit memory.
   - Return code.

2. **Lookup by code** (`GetTypeName(code)`):
   - Check in-memory cache → if found, return name.
   - If not found, reset cache (stale data assumed) and retry.
   - If still missing, return `code.ToString()` as a safe fallback.

## Why MurmurHash3?

Using a deterministic hash as the type code ensures:
- **Compact storage**: 8 bytes instead of variable-length strings.
- **Deterministic**: same type name always produces the same code across processes.
- **Collision-resistant enough** for type identification in an outbox context.
- **No auto-increment dependency**: avoids race conditions when multiple workers register types concurrently.

## Thread safety

- `_triggered` flag uses `Interlocked.CompareExchange` to ensure only one thread performs the DB insert + cache reset per missed lookup.
- Cache resets are lazy-loaded via `ResetLazy<Task<Storage>>`, preventing thundering herd on concurrent reads.

## DI Registration

```csharp
services.AddOutboxTypeResolver(); // registered internally
// Resolves to:
// IOutboxTypeCache     → OutboxTypeCache     (singleton)
// IOutboxTypeResolver  → OutboxTypeResolver  (singleton)
```
