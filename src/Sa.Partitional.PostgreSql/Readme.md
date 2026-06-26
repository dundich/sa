# Sa.Partitional.PostgreSql

Declarative PostgreSQL table partitioning library for .NET 10 — supports **range** (day / month / year) and **list** partitioning with automated migration, cleanup scheduling, and in-memory caching.

## Overview

Large PostgreSQL tables lose performance as they grow. This library automates the full partition lifecycle:

1. **Declare** partitioned tables declaratively via a fluent builder.
2. **Migrate** — automatically create missing partitions before data arrives.
3. **Cache** — keep partition metadata in memory to avoid repeated catalog queries.
4. **Clean up** — drop old partitions past a configurable retention window.

Everything wires into ASP.NET Core `IServiceCollection` through a single extension method.

## Quick Start

```csharp
builder.Services.AddSaPartitional((sp, builder) =>
{
    builder.AddSchema("public", schema =>
    {
        // Range-partitioned table (daily by default)
        schema.CreateTable("events")
            .PartByRange(PgPartBy.Day)
            .WithFillFactor(90);
    });
})
// Pre-create future partitions as a background job
.AddPartMigrationSchedule((sp, opts) => opts.AsBackgroundJob = true)
// Drop partitions older than 30 days
.AddPartCleanupSchedule((sp, opts) => opts.AsBackgroundJob = true);
```

## Supported Strategies

| Strategy | Description | Example |
|---|---|---|
| **Range** | Partitions by time intervals — day, month, or year | `events_y2026m06d26`, `events_y2026m07` |
| **List** | Partitions by discrete key values (strings or numbers) | `orders_RU`, `orders_USA` |

Both strategies can be combined hierarchically: a list-partitioned root can have range-partitioned children.

## Documentation

| Document | Contents |
|---|---|
| [Guide](Guide.md) | Configuration, fluent builder, StrOrNum, naming conventions, DDL examples |
| [API Reference](ApiReference.md) | Interface signatures, architecture diagram, key types |

## Project Details

- **Target framework:** `.NET 10.0`
- **Native AOT compatible:** Yes
- **Dependencies:** `Sa.Data.PostgreSql`, `Sa.Schedule`
- **License:** MIT
