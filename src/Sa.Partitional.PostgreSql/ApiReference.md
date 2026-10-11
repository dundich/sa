# API Reference

## Architecture Diagram

```
┌──────────────────────────────────────────────────────┐
│  AddSaPartitional(configure)                          │
│  AddSaPartitional(key, configure)   ← keyed module    │
│                                                      │
│  ┌─────────────┐  ┌──────────────┐  ┌────────────┐ │
│  │ Table Builder│  │ Part Cache   │  │ Migration  │ │
│  │ (ISettings) │  │ (PartCache)  │  │ Schedule   │ │
│  └──────┬──────┘  └──────┬───────┘  └─────┬──────┘ │
│         │                 │                │        │
│  ┌──────▼────────────────▼────────────────▼──────┐ │
│  │           IPartitionManager                   │ │
│  │   • Migrate()                                 │ │
│  │   • EnsureParts()                             │ │
│  └───────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────┘
         │              │               │
         ▼              ▼               ▼
  ┌───────────┐ ┌────────────┐ ┌──────────────┐
  │ Repository│ │ SQL Builder│ │ Cleanup Job  │
  │ (DDL exec)│ │ (template) │ │ (DROP parts) │
  └───────────┘ └────────────┘ └──────────────┘
```

## Registration

```csharp
// Keyless — the default module (one data source, one schedule).
public static IPartConfiguration AddSaPartitional(
    this IServiceCollection services,
    Action<IServiceProvider, ISettingsBuilder> configure,
    bool? AsBackgroundJob = null);

// Keyed — an independent module; every service of the module lives under `key`.
public static IPartConfiguration AddSaPartitional(
    this IServiceCollection services,
    string key,
    Action<IServiceProvider, ISettingsBuilder> configure,
    bool? AsBackgroundJob = null);
```

| Aspect | Keyless | Keyed |
|---|---|---|
| Services | unkeyed | keyed under `key`: `IPartitionManager`, `IPartRepository`, `IMigrationService`, `IPartCleanupService`, `ISettingsBuilder`, `ISqlBuilder`, `IPartCache`, settings |
| Schedule | default (unkeyed) schedule | named schedule `key` (`sp.GetRequiredKeyedService<IScheduler>(key)`) |
| Job ids / names | fixed | derived from `key` (`Migration job [key]`, `Cleanup job [key]`) |
| Data source | always registers the default one; `.AddDataSource(cfg)` configures it | named DS under `key` when configured via `.AddDataSource(cfg)`, else the default DS |
| Repeated calls | tables merge (jobs re-registered — pre-existing) | tables and settings merge; jobs registered once (per-key marker) |
| `AddDataSource()` in the chain | called internally (bare) | **not** called internally |

`key` must be non-empty and must not be `Sa.Schedule.Setup.DefaultName` (`"default"`), otherwise
`AddSaPartitional` throws `ArgumentException`.

## Key Interfaces

### `IPartitionManager`

Entry point for programmatic partition management. Registered as singleton via DI.

```csharp
public interface IPartitionManager
{
    /// <summary>Create any missing partitions across all configured tables.</summary>
    Task<int> Migrate(CancellationToken cancellationToken = default);

    /// <summary>Create partitions only for the specified dates.</summary>
    Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensure a specific partition exists; create it if absent.
    /// Checks the in-memory cache first, falls back to direct DDL if needed.
    /// </summary>
    Task<bool> EnsureParts(
        string tableName,
        DateTimeOffset date,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default);
}
```

**Usage:**

```csharp
public class MyService
{
    private readonly IPartitionManager _parts;

    public MyService(IPartitionManager parts) => _parts = parts;

    public async Task OnDataReceived(string tenant, DateTime now)
    {
        // Ensures partition for this tenant/date exists before writing data
        await _parts.EnsureParts("events", now, new StrOrNum[] { tenant });
    }
}
```

### `IPartRepository`

Low-level DDL executor — creates, queries, and drops partitions directly against PostgreSQL.

```csharp
public interface IPartRepository
{
    /// <summary>Creates a single partition (child table) for the given table, date, and values.</summary>
    Task<int> CreatePart(
        string tableName, DateTimeOffset date, StrOrNum[] partValues,
        CancellationToken cancellationToken = default);

    /// <summary>Ensures all tables have partitions covering each date in <paramref name="dates"/>.</summary>
    Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default);

    /// <summary>Same as above, but resolves list-partition values dynamically via <paramref name="resolve"/>.</summary>
    Task<int> Migrate(
        DateTimeOffset[] dates,
        Func<string, Task<StrOrNum[][]>> resolve,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves all range partitions starting from <paramref name="fromDate"/>.</summary>
    Task<List<PartByRangeInfo>> GetPartsFromDate(
        string tableName, DateTimeOffset fromDate,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves all range partitions up to and including <paramref name="toDate"/>.</summary>
    Task<List<PartByRangeInfo>> GetPartsToDate(
        string tableName, DateTimeOffset toDate,
        CancellationToken cancellationToken = default);

    /// <summary>Drops all partitions whose FromDate ≤ <paramref name="toDate"/>.</summary>
    Task<int> DropPartsToDate(
        string tableName, DateTimeOffset toDate,
        CancellationToken cancellationToken = default);
}
```

### `IMigrationService`

Automated pre-creation of future partitions. Runs on schedule or manually.

```csharp
public interface IMigrationService
{
    /// <summary>Triggered after a successful migration cycle completes.</summary>
    CancellationToken OnMigrated { get; }

    /// <summary>Migrates all tables for the configured forward-days window.</summary>
    Task<int> Migrate(CancellationToken cancellationToken = default);

    /// <summary>Migrates only for the specified dates.</summary>
    Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits synchronously (up to timeout) for an in-flight migration to complete.
    /// Returns immediately if migration has already finished.
    /// </summary>
    Task<bool> WaitMigration(TimeSpan timeout, CancellationToken cancellationToken = default);
}
```

### `IPartCleanupService`

Automatic removal of old partitions past the retention window.

```csharp
public interface IPartCleanupService
{
    /// <summary>Drops partitions older than the configured retention period.</summary>
    Task<int> Clean(CancellationToken cancellationToken);

    /// <summary>Drops all partitions with FromDate ≤ <paramref name="toDate"/>.</summary>
    Task<int> Clean(DateTimeOffset toDate, CancellationToken cancellationToken);
}
```

## Internal Components

| Component | File | Role |
|---|---|---|
| `PartitionManager` | `PartitionManager.cs` | Orchestrates cache + migration for `IPartitionManager` |
| `PartCache` | `Cache/PartCache.cs` | In-memory cache of partition metadata per table |
| `PartMigrationService` | `Migration/PartMigrationService.cs` | Scheduled migration executor with dedup guard |
| `PartCleanupService` | `Cleaning/PartCleanupService.cs` | Drops old partitions based on retention settings |
| `MigrationJob` | `Migration/MigrationJob.cs` | `IJob` wrapper for scheduled migrations |
| `PartCleanupJob` | `Cleaning/PartCleanupJob.cs` | `IJob` wrapper for scheduled cleanup |
| `SqlBuilder` | `SqlBuilder/SqlBuilder.cs` | Generates DDL templates from `ITableSettings` |
| `PartRepository` | `Partitional/PartRepository.cs` | Executes DDL via Npgsql |
| `JobKeys` | `JobKeys.cs` | Deterministic per-key job ids/names for keyed modules |
| `MigrationScheduleMarker` / `PartCleanupScheduleMarker` | `Migration/Setup.cs`, `Cleaning/Setup.cs` | Per-key sentinels so a repeated keyed registration does not duplicate jobs |
