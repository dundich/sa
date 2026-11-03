# Type Resolve — Sa.Outbox.PostgreSql

**English** | [Русский](Readme-ru.md)

## Contents

- [Purpose](#purpose)
- [Components](#components)
- [How it works](#how-it-works)
- [Why MurmurHash3?](#why-murmurhash3)
- [Thread safety](#thread-safety)
- [DI Registration](#di-registration)

## Purpose

The **Type Resolve** module provides a lightweight, cache-aware mechanism for mapping between fully qualified type names and compact integer codes (`long`) within the outbox system.

In the outbox architecture, every message type (e.g., `"OrderCreated"`, `"EmailSent"`) is stored in the `__type$` table as a `(long id, string typeName)` pair. Instead of storing the full type name string in every message record, the system stores only the numeric code — reducing storage footprint and improving join performance on partitioned tables.

## Components

| Class / Interface | Role |
|---|---|
| `IOutboxTypeCache` | Read-only cache layer over the `__type$` table. Loads all type mappings into memory on demand. |
| `OutboxTypeCache` | Concrete implementation. Holds bidirectional dictionaries (`long → string` and `string → long`) refreshed lazily via `ResetLazy`. |
| `IOutboxTypeResolver` | Higher-level resolver that coordinates cache hits, DB inserts, and hash computation. |
| `OutboxTypeResolver` | Orchestrates: check cache → compute MurmurHash3 fallback → insert into DB → refresh cache. Thread-safe via a `SemaphoreSlim` on the cold path. |
| `Setup.AddOutboxTypeResolver()` | DI extension that registers both services as singletons. Called automatically during `Sa.Outbox.PostgreSql.Setup`. |

## How it works

1. **Lookup by name** (`GetHashCode(typeName)`):
   - Check in-memory cache → if found, return code.
   - If not found, compute `MurmurHash3` of the type name → use as code.
   - Insert `(code, typeName)` into `__type$` table under a `SemaphoreSlim` (double-checked after the
     lock, so concurrent callers insert at most once).
   - Verify the stored name for that code; if the hash already belongs to another type name, throw
     `InvalidOperationException` instead of handing out a colliding code.
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

- The cold path is serialised by a `SemaphoreSlim` (not lock-free), so only one thread performs the DB
  insert + cache reset per missed lookup; the lock is taken only after the in-memory snapshot misses.
- Cache resets are lazy-loaded via `ResetLazy<Task<Storage>>`, preventing a thundering herd on
  concurrent reads.

## DI Registration

```csharp
// registered internally, once per provider key (the outbox scope key)
services.AddOutboxTypeResolver(providerKey);

// Resolves to, keyed by the same provider key:
// IOutboxTypeCache     → OutboxTypeCache     (keyed singleton)
// IOutboxTypeResolver  → OutboxTypeResolver  (keyed singleton)
sp.GetRequiredKeyedService<IOutboxTypeResolver>(providerKey);
```
