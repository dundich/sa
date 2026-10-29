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
    bool? asBackgroundJob = null);

// Keyed — an independent module; every service of the module lives under `key`.
public static IPartConfiguration AddSaPartitional(
    this IServiceCollection services,
    string key,
    Action<IServiceProvider, ISettingsBuilder> configure,
    bool? asBackgroundJob = null);
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

## Settings Configuration

`IPartConfiguration` exposes one method per scalar settings type; each takes a builder callback:

```csharp
public interface IPartConfiguration
{
    IPartConfiguration AddPartCache(
        Action<IPartSettingsBuilder<PartCacheSettings>>? configure = null);

    IPartConfiguration AddPartMigrationSchedule(
        Action<IPartSettingsBuilder<MigrationScheduleSettings>>? configure = null);

    IPartConfiguration AddPartCleanupSchedule(
        Action<IPartSettingsBuilder<PartCleanupScheduleSettings>>? configure = null);
}

public interface IPartSettingsBuilder<TSettings> where TSettings : class
{
    IPartSettingsBuilder<TSettings> FromConfiguration(string sectionPath);
    IPartSettingsBuilder<TSettings> Configure(Action<IServiceProvider, TSettings> configure);
    IPartSettingsBuilder<TSettings> Options(Action<OptionsBuilder<TSettings>> configureSettings);
}
```

The settings are standard **named options** — one instance per module: the module key for a keyed
module, the unnamed instance (`Options.DefaultName`) for the keyless one. The pipeline order is
**section → code → `Options(...)` actions → validation**, fixed regardless of where the calls sit in
the fluent chain. Validation is fail-fast through an `IValidateOptions<T>` validator registered
together with `ValidateOnStart()`, so an invalid section or code value surfaces as an
`OptionsValidationException` at host start and on the first read.

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
    /// Completes when the partition exists; failures surface as exceptions, so there is no result
    /// to inspect (a cache miss is not a failure).
    /// </summary>
    Task EnsureParts(
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

    public async Task OnDataReceived(string tenant, DateTimeOffset now)
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

    /// <summary>
    /// Detaches every partition whose FromDate ≤ <paramref name="toDate"/> and moves it, under the
    /// same name, into <paramref name="archiveSchemaName"/> (created when missing). Its cache row is
    /// marked archived instead of being deleted, so a repeated run is a no-op. PostgreSQL 13+.
    /// </summary>
    Task<int> ArchivePartsToDate(
        string tableName, DateTimeOffset toDate, string archiveSchemaName,
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
    /// Waits asynchronously (up to <paramref name="timeout"/>) for an in-flight migration to
    /// complete. Returns <c>true</c> immediately when a migration has already completed - the signal
    /// is one-shot, so it answers "has a migration ever completed". A timeout or a cancelled
    /// <paramref name="cancellationToken"/> returns <c>false</c> rather than throwing.
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

`Clean` follows `PartCleanupScheduleSettings.CleanupMode`: `Drop` deletes the partitions, `Archive`
detaches them and moves them into `PartCleanupScheduleSettings.ArchiveSchemaName` (see
`IPartRepository.ArchivePartsToDate`). Either way the in-memory partition cache is **not** invalidated
by a manual call, so the change becomes visible only after `PartCacheSettings.CacheTtl` - prefer the
scheduled job when the same process must see it immediately.
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
