# Sa.Partitional.PostgreSql — Guide

Detailed usage guide for configuring, tuning, and understanding Sa.Partitional.PostgreSql.

---

## Detailed Setup

### Range-Partitioned Table

Simplest case: a single table partitioned by day:

```csharp
builder.Services.AddSaPartitional((sp, builder) =>
{
    builder.AddSchema("public", schema =>
    {
        schema.CreateTable("events")
            .PartByRange(PgPartBy.Day)
            .WithFillFactor(90);
    });
})
.AddPartMigrationSchedule((sp, opts) =>
{
    opts.AsBackgroundJob = true;
    opts.ForwardDays = 2;
});
```

This generates:
- `events` — root table (range-partitioned on `created_at`)
- `events_y2026m06d26`, `events_y2026m06d27`, … — leaf partitions
- `events__part$` — metadata tracking table

### List + Hierarchical Partitioning

More complex scenario: list-partitioned root with range-partitioned children:

```csharp
builder.Services.AddSaPartitional((sp, builder) =>
{
    builder.AddSchema("public", schema =>
    {
        schema.AddTable("customer",
                "id uuid DEFAULT gen_random_uuid()",
                "country text NOT NULL",
                "city text NOT NULL")
            .WithPartSeparator("_")
            .PartByList("country", "city")
            .AddMigration("RU", "Moscow", "Samara")
            .AddMigration("USA", "Alabama", "New York")
            .AddMigration("FR", "Paris", "Lyon");
    });
})
.AddPartMigrationSchedule((sp, opts) =>
{
    opts.AsBackgroundJob = true;
    opts.ForwardDays = 2;
})
.AddPartCleanupSchedule((sp, opts) =>
{
    opts.AsBackgroundJob = true;
    opts.DropPartsAfterRetention = TimeSpan.FromDays(30);
});
```

This generates a multi-level hierarchy:
```
customer (root, LIST on country)
├── customer_FR (LIST on city)
│   ├── customer_FR_Bordeaux (RANGE on created_at)
│   │   ├── customer_FR_Bordeaux_y2026m06d26
│   │   └── customer_FR_Bordeaux_y2026m06d27
│   ├── customer_FR_Lyon_y2026m06d26
│   └── customer_FR_Paris_y2026m06d26
├── customer_RU (LIST on city)
│   ├── customer_RU_Moscow_y2026m06d26
│   └── customer_RU_Samara_y2026m06d26
└── customer_USA (...)
```

---

## Configuration Options

### Migration Schedule (`MigrationScheduleSettings`)

| Property | Default | Description |
|---|---|---|
| `ForwardDays` | `2` | How many days ahead to pre-create partitions on each run |
| `AsBackgroundJob` | `false` | Run as a hosted background service |
| `ExecutionInterval` | `4h + jitter` | Interval between runs (adds 1–59 min random jitter) |
| `WaitMigrationTimeout` | `3s` | Max wait time when two callers race for the same migration |
| `MigrationJobName` | `"Migration job"` | Display name in the job scheduler |

### Cleanup Schedule (`PartCleanupScheduleSettings`)

| Property | Default | Description |
|---|---|---|
| `DropPartsAfterRetention` | `30 days` | Partitions older than this are dropped |
| `AsBackgroundJob` | `false` | Run as a hosted background service |
| `ExecutionInterval` | `4h + jitter` | Interval between cleanup runs |
| `InitialDelay` | `1 min` | Delay before first execution |

### Cache Settings (`PartCacheSettings`)

| Property | Default | Description |
|---|---|---|
| `CachedFromDate` | `1 day` | How far ahead from now the cache preloads partition metadata |

The cache avoids repeated catalog queries (`pg_class`, `pg_partitioned_table`). When a partition is created at runtime via `EnsureParts`, the cache is invalidated and reloaded automatically.

---

## Fluent Table Builder

The `ITableBuilder` interface supports a fluent DSL:

```csharp
schema.CreateTable("orders")
    // 1. Choose partitioning strategy
    .PartByRange(PgPartBy.Month)          // range by month (default granularity)
    // or
    .PartByList("tenant_id", "region")    // list by column values

    // 2. Override timestamp column (auto-detected by default)
    .TimestampAs("shipped_at")

    // 3. Tuning knobs
    .WithFillFactor(85)                    // WITH (fillfactor = 85) for HOT updates
    .WithPartSeparator("_")               // separator in partition names (default: _)
    .WithPartTablePostfix("__part")       // child table suffix (default: __part)

    // 4. Custom SQL hooks
    .AddPostSql(() => "INCLUDE (extra_column)")
    .AddConstraintPkSql(() => $"CONSTRAINT pk_orders PRIMARY KEY (id, tenant_id, shipped_at)")

    // 5. Declare partition migrations
    .AddMigration(new StrOrNum[] { "US", "EU", "APAC" })  // static values
    .AddMigration(myMigrationProvider)                      // IPartTableMigrationSupport
    .AddMigration(async ct =>                              // lazy async resolver
    {
        var rows = await QueryDb(ct);
        return rows.Select(r => new StrOrNum[] { r.Key }).ToArray();
    })

    // 6. Finalise
    .Build();
```

### Migration Variants

Three ways to declare which partitions to create:

| Variant | Signature | When to use |
|---|---|---|
| **Static** | `.AddMigration(params StrOrNum[] partValues)` | Fixed set known at compile time |
| **Parent+Children** | `.AddMigration(StrOrNum parent, StrOrNum[] childs)` | Hierarchical list partitions |
| **Dynamic** | `.AddMigration(Func<CancellationToken, Task<StrOrNum[][]>> getPartValues)` | Values come from another table / API |
| **Interface** | `.AddMigration(IPartTableMigrationSupport support)` | Reusable migration provider class |

---

## Type-Safe Partition Keys: `StrOrNum`

Partition values may be strings (e.g. country codes) or numbers (e.g. tenant IDs). The `StrOrNum` discriminated union handles both:

```csharp
// Implicit conversion from string or numeric types
StrOrNum country = "RU";          // ChoiceStr
StrOrNum tenant = 42L;            // ChoiceNum

// Pattern-match on the active variant
string description = tenant.Match(
    onChoiceStr: s => $"text: {s}",
    onChoiceNum: n => $"numeric: {n}"
);

// Round-trip serialization (used by JSON converter)
StrOrNum parsed = StrOrNum.FromFmtStr("s:hello");  // ChoiceStr
StrOrNum number   = StrOrNum.FromFmtStr("n:123");   // ChoiceNum

// Parse raw spans safely (culture-independent)
long? value = StrOrNum.StrToLong("42".AsSpan());   // 42
```

### JSON Serialization

`StrOrNumConverter` serialises values using a prefixed format:
- Strings → `"s:value"`
- Numbers → `"n:123"`

This ensures type fidelity across JSON round-trips.

---

## Partition Naming Convention

PostgreSQL limits table names to **63 characters**. To stay within this limit, every partition name ends with an `int64` Unix-timestamp segment:

| Strategy | Timestamp Format | Example |
|---|---|---|
| Day | `yYYYYmmDD` | `y2026m06d26` |
| Month | `yYYYYmm` | `y2026m06` |
| Year | `yYYYY` | `y2026` |

Full partition name pattern: `{schema}{separator}{tableName}{postfix}{separator}{key}_{timestamp}`

For example: `public.customer__RU_Yokohama_y2026m06d26`

### Name Length Limits

When combined, the full name must fit within 63 characters:

```
public._customer__RU_Yokohama_y2026m06d26
 ^6 _^8    ^^4   ^^8         ^^^^^^^^^^
 6 + 1 + 8 + 1 + 4 + 1 + 8 + 1 + 10 = 40 chars ✓
```

If your keys are long, consider reducing `WithPartSeparator` length or using shorter key values.

---

## Date Ranges

Each partition covers an inclusive-exclusive `[from, to)` interval computed from the `PgPartBy` strategy:

```
Day:   [2026-06-26 00:00 UTC, 2026-06-27 00:00 UTC)
Month: [2026-06-01 00:00 UTC, 2026-07-01 00:00 UTC)
Year:  [2026-01-01 00:00 UTC, 2027-01-01 00:00 UTC)
```

All computations use UTC. The `PgPartBy` record stores three delegates:
- `GetRange` — computes the `LimSection<DateTimeOffset>` for a given date
- `Fmt` — formats a date into a partition name string
- `ParseFmt` — parses a partition name back into a `DateTimeOffset`

---

## Generated DDL Example

Given this configuration:

```csharp
schema.AddTable("events",
    "tenant_id text NOT NULL",
    "created_at timestamptz NOT NULL")
    .PartByList("tenant_id")
    .PartByRange(PgPartBy.Day, "created_at")
    .AddMigration("analytics", "api", "web");
```

The library generates:

```sql
-- Root table (list-partitioned on tenant_id)
CREATE TABLE public.events (
    id uuid DEFAULT gen_random_uuid(),
    tenant_id text NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT pk_events PRIMARY KEY (id, tenant_id, created_at)
) PARTITION BY LIST (tenant_id);

-- First-level children (list partition by tenant)
CREATE TABLE public."events_analytics" PARTITION OF public.events FOR VALUES IN ('analytics')
    PARTITION BY RANGE (created_at);

CREATE TABLE public."events_api" PARTITION OF public.events FOR VALUES IN ('api')
    PARTITION BY RANGE (created_at);

CREATE TABLE public."events_web" PARTITION OF public.events FOR VALUES IN ('web')
    PARTITION BY RANGE (created_at);

-- Leaf partitions (range by day)
CREATE TABLE public."events_analytics_y2026m06d26" PARTITION OF public."events_analytics"
    FOR VALUES FROM ('1750896000') TO ('1750982400');

CREATE TABLE public."events_analytics_y2026m06d27" PARTITION OF public."events_analytics"
    FOR VALUES FROM ('1750982400') TO ('1751068800');
```

Additionally, a metadata tracking table is created:

```sql
CREATE TABLE public."events__part$" (
    id text NOT NULL,
    root text NOT NULL,
    part_values text NOT NULL,
    part_by text NOT NULL,
    from_date int8 NOT NULL,
    to_date int8 NOT NULL,
    CONSTRAINT "events__part$_pkey" PRIMARY KEY (id)
);
```

---

## Error Handling

Both the migration and cleanup jobs suppress errors by default (`DoSuppressError`) so that transient database unavailability does not crash the application. Errors are logged through the standard `ILogger` pipeline.

To customise error handling, register your own settings action **after** the default registration — settings are merged via `IServiceProvider.GetServices<>()`:

```csharp
.AddPartMigrationSchedule((sp, opts) =>
{
    opts.AsBackgroundJob = true;
    opts.ForwardDays = 3;
    // Additional overrides apply after defaults
})
```

---

## Multiple Schemas

You can declare tables across multiple schemas:

```csharp
builder.AddSaPartitional((sp, builder) =>
{
    builder
        .AddSchema(defaultSchema =>
        {
            defaultSchema.CreateTable("logs")
                .PartByRange(PgPartBy.Day);
        })
        .AddSchema("archive", archiveSchema =>
        {
            archiveSchema.AddTable("audit_log",
                    "user_id text NOT NULL",
                    "occurred_at timestamptz NOT NULL")
                .PartByList("user_id")
                .PartByRange(PgPartBy.Month, "occurred_at");
        });
});
```

---

## Custom Primary Key Constraints

Override the auto-generated primary key constraint:

```csharp
schema.AddTable("events",
        "tenant_id text NOT NULL",
        "event_id uuid NOT NULL",
        "created_at timestamptz NOT NULL")
    .PartByRange(PgPartBy.Day, "created_at")
    .AddConstraintPkSql(() =>
        $"CONSTRAINT pk_events PRIMARY KEY (event_id, tenant_id, created_at)")
    .Build();
```

## Project Details

- **Target framework:** `.NET 10.0`
- **Native AOT compatible:** Yes
- **Dependencies:** `Sa.Data.PostgreSql`, `Sa.Schedule`
- **License:** MIT
