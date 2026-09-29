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

1. Cuts the batch into chunks that fit within a safe budget — up to `DefaultMaxLen` (1024) elements, sized from the measured 8 (+4 reused common) parameters per row for `SqlFinishDelivery` and 4 for `SqlError`. A 1024-row finish chunk binds 8196 parameters = 12.5% of the limit; the earlier "~16 params per element" estimate was wrong by a factor of 2–4 (see `SqlOutboxBuilderTests.BulkStatements_ParameterPerRowRatio_Is8And4_Not16`).
2. Aligns chunk boundaries to multiples of 16 so chunk sizes stay within the pre-allocated parameter-name cache.
3. Caches generated SQL templates by length so identical sizes reuse the same string instance.

The cache is bounded to at most **79 entries** (multiples of 16 up to 1024 + remainders 1–15). Worst case, with every key generated, that is a few MB of SQL text; a real consumer exercises only 2–4 distinct sizes, so in practice it is a handful of strings — never unbounded.

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
