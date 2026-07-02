# Sa.Partitional.PostgreSql

Declarative PostgreSQL table partitioning library for .NET 10 — supports **range** (day / month / year) and **list** partitioning with automated migration, cleanup scheduling, and in-memory caching.

---

## Overview

Large PostgreSQL tables lose performance as they grow. This library automates the full partition lifecycle:

1. **Declare** partitioned tables declaratively via a fluent builder.
2. **Migrate** — automatically create missing partitions before data arrives.
3. **Cache** — keep partition metadata in memory to avoid repeated catalog queries.
4. **Clean up** — drop old partitions past a configurable retention window.

Everything wires into ASP.NET Core `IServiceCollection` through a single extension method.

---

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

---

## Supported Strategies

| Strategy | Description | Example |
|---|---|---|
| **Range** | Partitions by time intervals — day, month, or year | `events_y2026m06d26`, `events_y2026m07` |
| **List** | Partitions by discrete key values (strings or numbers) | `orders_RU`, `orders_USA` |

Both strategies can be combined hierarchically: a list-partitioned root can have range-partitioned children.

---

## Fluent Builder API

### Schema + Table Declaration

```csharp
services.AddSaPartitional((sp, builder) =>
{
    builder.AddSchema("outbox", schema =>
    {
        // Range-partitioned by day
        schema.CreateTable("messages")
            .AddFields("tenant_id varchar(50) NOT NULL")
            .PartByRange(PgPartBy.Day, "created_at")
            .WithFillFactor(80);

        // List-partitioned by tenant
        schema.CreateTable("orders")
            .AddFields("region varchar(10) NOT NULL")
            .PartByList("region")
            .AddMigration("EU", "US", "APAC");
    });
});
```

### ITableBuilder Methods

| Method | Description |
|--------|-------------|
| `AddFields(params string[])` | Column definitions (e.g., `"tenant_id varchar(50) NOT NULL"`) |
| `PartByRange(PgPartBy, fieldName?)` | Range partitioning strategy (Day/Month/Year) |
| `PartByList(params string[])` | List partitioning on column(s) |
| `TimestampAs(fieldName)` | Override timestamp column name (default: `created_at`) |
| `WithPartSeparator(string)` | Separator between parts in names (default: `"__"`) |
| `WithFillFactor(int)` | PostgreSQL fill factor storage parameter |
| `WithPartTablePostfix(string)` | Suffix for cache/partition tables (default: `"__part"`) |
| `AddPostSql(Func<string>)` | Extra SQL after CREATE TABLE |
| `AddConstraintPkSql(Func<string>)` | Custom CHECK / PK constraint SQL |
| `AddMigration(IPartTableMigrationSupport)` | Provide custom migration values |
| `AddMigration(Func<CancellationToken, Task<StrOrNum[][]>>)` | Async factory for migration values |
| `AddMigration(params StrOrNum[])` | Inline list partition values |
| `AddMigration(StrOrNum parent, StrOrNum[] childs)` | Hierarchical migration (parent + children) |
| `Build()` | Finalize table settings |

---

## Key Types

### IPartitionManager

Main entry point for programmatic partition management:

```csharp
public interface IPartitionManager
{
    Task<int> Migrate(CancellationToken ct = default);
    Task<int> Migrate(DateTimeOffset[] dates, CancellationToken ct = default);
    Task<bool> EnsureParts(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken ct = default);
}
```

- `Migrate()` — pre-create all missing partitions for today + forward window
- `Migrate(dates[])` — pre-create for specific dates only
- `EnsureParts()` — guarantee a specific partition exists (creates it if missing)

### PgPartBy

Partitioning strategy enum with three predefined values:

| Value | Format Pattern | Example Name | Range |
|-------|---------------|--------------|-------|
| `PgPartBy.Day` | `yYYYYmmDD` | `events_y2026m06d26` | StartOfDay → +1 day |
| `PgPartBy.Month` | `yYYYYmm` | `events_y2026m07` | StartOfMonth → +1 month |
| `PgPartBy.Year` | `yYYYY` | `events_y2026` | StartOfYear → +1 year |

Additional factory methods:
```csharp
PgPartBy.FromRange(PartByRange.Day);   // from PartByRange enum
PgPartBy.FromPartName("root");         // from partition name string
```

### StrOrNum

Discriminated union for list partition keys — supports both string and numeric values:

```csharp
// Implicit conversions
StrOrNum s = "tenant_a";     // → ChoiceStr
StrOrNum n = 42L;            // → ChoiceNum

// Pattern matching
result.Match(
    onChoiceStr: v => Console.WriteLine($"String: {v}"),
    onChoiceNum: v => Console.WriteLine($"Number: {v}")
);

// Formatting
s.ToFmtString();             // "s:tenant_a"
StrOrNum.FromFmtStr("n:42"); // → ChoiceNum(42)
```

Supported implicit conversions: `string`, `int`, `long`, `short`.

---

## Schedule Settings

### MigrationScheduleSettings

Controls automatic pre-creation of future partitions:

| Property | Default | Description |
|----------|---------|-------------|
| `ForwardDays` | `2` | Days ahead to pre-create partitions |
| `AsBackgroundJob` | `false` | Run as hosted service |
| `MigrationJobName` | `"Migration job"` | Job name identifier |
| `ExecutionInterval` | `~4h + jitter` | Interval between migrations |
| `WaitMigrationTimeout` | `3 sec` | Semaphore wait timeout |

### PartCleanupScheduleSettings

Controls automatic dropping of old partitions:

| Property | Default | Description |
|----------|---------|-------------|
| `DropPartsAfterRetention` | `30 days` | Age threshold for deletion |
| `AsBackgroundJob` | `false` | Run as hosted service |
| `ExecutionInterval` | `~4h + jitter` | Interval between cleanups |
| `InitialDelay` | `1 min` | Delay before first run |

### PartCacheSettings

In-memory partition metadata cache:

| Property | Default | Description |
|----------|---------|-------------|
| `CachedFromDate` | `1 day` | How far ahead to preload partitions |

---

## Naming Conventions

Partitions follow predictable naming patterns (separator defaults to `"__"`):

| Component | Pattern | Example |
|-----------|---------|---------|
| Range (day) | `{table}{sep}y{YYYY}m{MM}d{DD}` | `events__part__y2026m06d26` |
| Range (month) | `{table}{sep}y{YYYY}m{MM}` | `events__part__y2026m07` |
| Range (year) | `{table}{sep}y{YYYY}` | `events__part__y2026` |
| List (nested) | `{table}{sep}{val1}_{val2}...` | `orders__part__EU_EU_1` |
| Cache table | `{table}{postfix}` | `events__part` |

**Constraints:**
- Identifiers must not exceed 63 characters (PostgreSQL limit).
- Schemas are auto-created via `CREATE SCHEMA IF NOT EXISTS`.
- Child partitions use `PARTITION OF parent FOR VALUES FROM (...) TO (...)` (range) or `FOR VALUES IN (...)` (list).
- After each range partition is created, a cache table tracks boundaries via `INSERT ... ON CONFLICT (id) DO NOTHING`.

---

## Architecture

```
┌─────────────────────┐
│  IPartitionManager  │  ← Public entry point
├─────────────────────┤
│  IMigrationService  │  ← Pre-create future partitions
│  IPartCleanupService│  ← Drop old partitions
├─────────────────────┤
│  IPartRepository    │  ← DDL execution (CREATE/DROP PARTITION)
│  ISqlBuilder        │  ← SQL template generation
│  IPartCache         │  ← In-memory metadata cache
├─────────────────────┤
│  MigrationJob       │  ← IJob wrapper for Sa.Schedule
│  PartCleanupJob     │  ← IJob wrapper for Sa.Schedule
└─────────────────────┘
```

---

## Dependencies

- `Sa.Data.PostgreSql` — Npgsql client wrapper with retry strategy
- `Sa.Schedule` — Background job scheduling infrastructure

---

## Project Layout

```
src/Sa.Partitional.PostgreSql/
├── Setup.cs                        # Main DI entrypoint AddSaPartitional()
├── IPartitionManager.cs            # Public partition management API
├── PgPartBy.cs                     # Partitioning strategy enum
├── Classes/
│   ├── StrOrNum.cs                 # Discriminated union (string | long)
│   └── Enumeration.cs              # Type-safe base enum pattern
├── Configuration/                  # Fluent builder API
│   ├── IPartConfiguration.cs
│   └── Builder/                    # ISettingsBuilder, ISchemaBuilder, ITableBuilder
├── Settings/                       # ITableSettings, ITableSettingsStorage
├── Cache/                          # In-memory cache: PartCache, PartCacheSettings
├── Migration/                      # Pre-creation: IMigrationService, MigrationJob
├── Cleaning/                       # Old-partition removal: IPartCleanupService, PartCleanupJob
├── Partitional/                    # DDL repo: IPartRepository, PartByRangeInfo
└── SqlBuilder/                     # SQL templates: ISqlBuilder, SqlTemplate.cs
```

---

## License

MIT
