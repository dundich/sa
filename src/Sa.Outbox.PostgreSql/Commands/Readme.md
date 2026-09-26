# Commands

## Overview

This folder contains the data-access commands that translate high-level outbox operations into concrete PostgreSQL statements. Each command owns its own `SqlCacheSplitter` to handle batching transparently.

| File | Responsibility |
|---|---|
| `BulkInsertMsgCommand.cs` | Bulk-inserts messages into the outbox table via PostgreSQL BINARY COPY |
| `FinishDeliveryCommand.cs` | Marks delivered messages as finished (OK / Accepted / NoContent) |
| `ErrorDeliveryCommand.cs` | Records permanent errors from failed deliveries |
| `ExtendDeliveryCommand.cs` | Extends the lock duration on in-flight delivery records |
| `StartDeliveryCommand.cs` | Creates initial delivery task records in the task queue |
| `SelectTenantCommand.cs` | Selects active tenant identifiers |
| `SelectMsgTypeCommand.cs` | Selects registered message types from the type registry |
| `InsertMsgTypeCommand.cs` | Inserts new message-type registrations |
| `NpgsqlOutboxReader.cs` | Low-level reader helpers for scanning outbox tables |
| `NpgsqlCommandExtension.cs` | Extension methods for adding typed parameters to `NpgsqlCommand` |
| `Setup.cs` | DI setup helpers for registering commands |

## SqlCacheSplitter

The heart of batch splitting logic lives in `SqlCacheSplitter.cs`. See the class XML documentation for details.

### Why it exists

PostgreSQL has a hard limit of **65 535 parameters** per statement. When processing hundreds or thousands of outbox messages in a single batch, the generated SQL would easily exceed this limit. The splitter:

1. Cuts the batch into chunks that fit within a safe budget (≤ 512 elements).
2. Aligns chunk boundaries to multiples of 16 because each element adds ~16 SQL parameters.
3. Caches generated SQL templates by length so identical sizes reuse the same string instance.

The cache is bounded to at most **47 entries** (multiples of 16 up to 512 + remainders 1–15), consuming roughly **50 KB** — negligible and never unbounded.

### How callers use it

```csharp
foreach ((string sql, int length) in _sqlCache.GetSql(messages.Length))
{
    var slice = messages.Slice(startIndex, length);
    startIndex += length;
    // execute sql with slice of messages
}
```

Each iteration yields one SQL template and how many elements it covers. The caller slices the input collection accordingly and executes the command.
