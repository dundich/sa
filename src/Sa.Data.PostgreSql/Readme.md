# Sa.Data.PostgreSql

Lightweight Npgsql wrapper for common PostgreSQL operations — no ORM overhead, with DI, Native AOT support, and minimal allocations.

---

## Quick Start

```csharp
// Option 1: direct creation
var dataSource = IPgDataSource.Create("Host=db;Database=mydb;Username=usr;Password=pwd");
// or from an existing NpgsqlDataSource — shares its connection pool
var dataSource = IPgDataSource.Create(new NpgsqlDataSource("Host=db;Database=mydb;Username=usr;Password=pwd"));

// Option 2: via DI — the connection string goes through the standard options pipeline
services.AddSaPostgreSqlDataSource(b => b
    .Options(ob => ob.Configure(o => o.ConnectionString = "Host=db;Database=mydb;Username=usr;Password=pwd")));

// Option 3: from a configuration section
services.AddSaPostgreSqlDataSource(b => b.FromConfiguration("Postgres"));
```

---

## Registration (DI)

### With DI

`AddSaPostgreSqlDataSource` takes the standard options callback, so configuration goes through `Configure` / `PostConfigure` / `Validate` like any other options type — not through a bespoke overload:

```csharp
services.AddSaPostgreSqlDataSource(b => b
    .Options(ob => ob.Configure(o => o.ConnectionString = "Host=db;Database=mydb;Username=usr;Password=pwd")));

// Usage:
var dataSource = serviceProvider.GetRequiredService<IPgDataSource>();
```

The only option is `ConnectionString`. Pooling, timeout, and search path are configured inside the string itself (e.g. `Host=…;Minimum Pool Size=5;Maximum Pool Size=100;Search Path=storage;Timeout=15`).

### From a configuration section

```csharp
// appsettings.json:
// { "Postgres": { "ConnectionString": "Host=…;Database=…;Username=…;Password=…" } }
services.AddSaPostgreSqlDataSource(b => b.FromConfiguration("Postgres"));
```

The section binds in a fixed slot **first**, so an `Options(...)` `Configure` has the last word. The `Options(...)` actions replay after the registration's own `PostConfigure` and `Validate`, so your checks add to the built-in ones instead of replacing them.

### Reusing an existing NpgsqlDataSource

When `ConnectionString` is empty, the data source is built from an `NpgsqlDataSource` registered in DI — sharing its connection pool instead of opening a second one. The wrapper never disposes it; ownership stays with the registration:

```csharp
services.AddSingleton(new NpgsqlDataSource("Host=db;Database=mydb;Username=usr;Password=pwd"));
services.AddSaPostgreSqlDataSource();
```

On a keyed instance, an empty string likewise falls back to the `NpgsqlDataSource` registered with the same key.

### Multiple databases (keyed)

One container can hold several independent data sources — one per key. Each key gets its own named `PgDataSourceOptions` instance (its own connection string) and its own keyed `IPgDataSource`; resolve with `GetRequiredKeyedService<IPgDataSource>(key)`:

```csharp
services.AddSaPostgreSqlDataSource("orders", b => b
    .Options(ob => ob.Configure(o => o.ConnectionString = "Host=orders;Database=orders;Username=usr;Password=pwd")));
services.AddSaPostgreSqlDataSource("billing", b => b
    .Options(ob => ob.Configure(o => o.ConnectionString = "Host=billing;Database=billing;Username=usr;Password=pwd")));

// Usage:
var orders = serviceProvider.GetRequiredKeyedService<IPgDataSource>("orders");
var billing = serviceProvider.GetRequiredKeyedService<IPgDataSource>("billing");
```

Named options are read through `IOptionsMonitor<PgDataSourceOptions>.Get(key)`. The default data source (the `string.Empty` key, also what the keyless call registers) additionally gets an unnamed alias, so `GetRequiredService<IPgDataSource>()` and `GetRequiredKeyedService<IPgDataSource>(string.Empty)` return the same instance.

The `string.Empty` key is the original unnamed data source: the one-argument `AddSaPostgreSqlDataSource(configure)` delegates to `AddSaPostgreSqlDataSource(string.Empty, configure)`, so existing code is unchanged.

### Validation

The connection string is normalised (trimmed) in `PostConfigure` and then validated on the way out: a non-empty value must parse as a PostgreSQL connection string (checked with the same `NpgsqlConnectionStringBuilder` Npgsql uses, so a typo in `Host=` surfaces as an early `OptionsValidationException` rather than a bare `FormatException` from the middle of the first query); an empty value is only valid when an `NpgsqlDataSource` is registered in the collection.

`ValidateOnStart()` turns a bad configuration into an `OptionsValidationException` at host start, and the same check fires when `IOptions<PgDataSourceOptions>.Value` is first read — i.e. on the first data-source resolve — so even a bare `ServiceCollection` fails fast instead of deep in a query.

The validator is an explicit `IValidateOptions<PgDataSourceOptions>` rather than `ValidateDataAnnotations()`: the latter is marked `RequiresUnreferencedCode` (IL2026) and breaks Native AOT, which this assembly is built for.

Register the data source with configuration only once **per key** — a second *configuring* call (one carrying a `configure` callback — the section comes via `FromConfiguration`, the settings via `Options(...)`) for the same key throws `InvalidOperationException`, because both `Configure` callbacks would otherwise apply to the same options instance and the settings would silently merge. A call for a different key is always allowed — that is the point of keyed registration. Bare calls (no callback at all) may be repeated by design: `AddSaPartitional` and `AddSaOutboxUsingPostgreSql` each register a bare data source, and a single configuring call — wherever it sits — is the one that takes effect.

---

## ExecuteNonQuery

Executes a SQL command that doesn't return data (INSERT / UPDATE / DELETE / DDL) and returns the number of affected rows.

```csharp
// Simple query
int affected = await dataSource.ExecuteNonQuery("DELETE FROM sessions WHERE expired = true");

// With parameters
int affected = await dataSource.ExecuteNonQuery("""
    INSERT INTO users (name, age) VALUES (@p0, @p1);
    """, [
        new NpgsqlParameter { ParameterName = "p0", Value = "Tom" },
        new NpgsqlParameter { ParameterName = "p1", Value = 18 }
    ]);
```

---

## ExecuteScalar / ExecuteScalarTyped

Returns the first value of the first row in the result. `ExecuteScalarTyped<T>` automatically casts the result, including support for `Guid`, `DateTime`, `DateTimeOffset`, and `DateOnly → DateTime`.

```csharp
// object? overload
object? count = await dataSource.ExecuteScalar("SELECT COUNT(*) FROM users");

// Typed overload
int count = await dataSource.ExecuteScalarTyped<int>("SELECT COUNT(*) FROM users");
long id = await dataSource.ExecuteScalarTyped<long>("SELECT nextval('users_id_seq')");
Guid tenantId = await dataSource.ExecuteScalarTyped<Guid>("SELECT tenant_uuid FROM tenants LIMIT 1");
```

---

## ExecuteReader

Streaming row reading with a callback — ideal for processing large results without loading into memory.

```csharp
int processed = 0;
await dataSource.ExecuteReader("SELECT id, name FROM users", (reader, rowIndex) =>
{
    int id = reader.GetInt32(0);
    string name = reader.GetString(1);
    Console.WriteLine($"{rowIndex}: {id} → {name}");
    processed++;
});
Console.WriteLine($"Processed {processed} rows");
```

---

## ExecuteReaderList

Reads all rows and collects them into `List<T>`.

```csharp
// Simple projection
var names = await dataSource.ExecuteReaderList<string>(
    "SELECT name FROM users ORDER BY name",
    reader => reader.GetString(0));

// With parameters
var activeUsers = await dataSource.ExecuteReaderList<(int Id, string Name)>(
    """SELECT id, name FROM users WHERE active = @active ORDER BY name""",
    reader => (reader.GetInt32(0), reader.GetString(1)),
    [new NpgsqlParameter { ParameterName = "active", Value = true }]);
```

---

## ExecuteReaderFirst

Returns the value of the first column of the **first** row. Only that row is fetched from the server — the rest of the result set is not read. Returns `default(T)` if the query returns no rows or the value is `NULL`.

Supported types: `int`, `long`, `short`, `byte`, `bool`, `double`, `float`, `decimal`, `char`, `string`, `DateTime`, `Guid`, `DateTimeOffset`, `TimeSpan`, `DateOnly`, `TimeOnly`, plus enums, any `Nullable<T>` of these and any other `IConvertible` type.

```csharp
// Returns 0 if the table is empty
int errorCount = await dataSource.ExecuteReaderFirst<int>(
    "SELECT COUNT(*) FROM outbox_errors");

// Guid — automatic casting works (LIMIT is no longer required: only the first row is read)
Guid firstTenantId = await dataSource.ExecuteReaderFirst<Guid>(
    "SELECT tenant_id FROM tenants ORDER BY created_at");

// Nullable and enums are supported too
int? maybeAge = await dataSource.ExecuteReaderFirst<int?>(
    "SELECT age FROM users WHERE id = 42");
```

---

## BeginBinaryImport

Fast binary data import via COPY — orders of magnitude faster than individual INSERTs.

```csharp
ulong imported = await dataSource.BeginBinaryImport(
    "COPY bulk_data (id, payload, created_at) FROM STDIN BINARY",
    async (writer, ct) =>
    {
        foreach (var item in items)
        {
            writer.StartRow();
            writer.Write(item.Id, NpgsqlDbType.Integer);
            writer.Write(item.Payload, NpgsqlDbType.Bytea);
            writer.Write(item.CreatedAt.ToUnixTimeMilliseconds(), NpgsqlDbType.Timestamp);
        }
        return await writer.CompleteAsync(ct);
    },
    cancellationToken);

Console.WriteLine($"Imported {imported} rows");
```

---

## PgRetryStrategy

Retry with jitter for transient Npgsql errors.

```csharp
using Sa.Data.PostgreSql;

// Automatically retries on transient errors (connection reset, timeout, etc.)
var result = await PgRetryStrategy.ExecuteWithRetry(
    async ct =>
    {
        using var conn = await dataSource.OpenDbConnection(ct);
        return await conn.OpenAsync(ct);
    },
    retryCount: 5,
    initialDelay: 530);
```

---

## PgTransaction (IPgDataSource.BeginTransaction)

`IPgDataSource.BeginTransaction(...)` opens a PostgreSQL transaction on the data source without juggling connection lifetime — the returned `PgTransaction` owns the connection and the transaction, and returns the connection to the pool on dispose.

```csharp
using Sa.Data.PostgreSql;

IPgDataSource dataSource = services.GetRequiredService<IPgDataSource>();

await using PgTransaction sqlTx = await dataSource.BeginTransaction(ct);

// Reuse the same connection + transaction for several components of one unit of work,
// e.g. an outbox write that must commit atomically with your own inserts:
await somethingWritingInTheTransaction(sqlTx.Transaction, ct);

await sqlTx.CommitAsync(ct);        // commit → all writes visible together
// dispose without commit → rollback, connection returned to the pool
```

- `BeginTransaction` is a regular member of the `IPgDataSource` contract; `PgDataSource` implements it on top of `OpenDbConnection`.
- The transaction targets whichever data source the `IPgDataSource` instance represents — for the default (unnamed) data source that is the shared/default one.
- `PgTransaction.Connection` — the open connection, `PgTransaction.Transaction` — its active transaction.
- Dispose rolls back an uncommitted transaction (best effort) and returns the connection to the pool; `CommitAsync` / `RollbackAsync` mark it completed so dispose is then a no-op for the transaction itself.
- `IPgDataSource.GetConnectionString()` returns the underlying connection string of the data source (Npgsql omits the password unless `Persist Security Info` is set) — used, for example, to verify that a caller-supplied transaction targets the same database.

---

## DbCommandExtensions + INamePrefixProvider

Optimized API for adding parameterized commands with pre-cached parameter names (minimal allocations).

```csharp
// Declare a prefix provider
public class UserParams : INamePrefixProvider
{
    public static string[] GetPrefixes() => ["name", "age", "email"];
    public static int MaxIndex => 10;
}

// Use — names are generated as @name0, @name1, ..., @age0, ...
var cmd = new NpgsqlCommand("SELECT * FROM users WHERE name = @name0 AND age > @age0")
    .AddParam<UserParams>("name", "Tom", 0)
    .AddParam<UserParams>("age", 18, 0);
```

---

## Method Comparison

| Method | Return | When to use |
|--------|--------|-------------|
| `ExecuteNonQuery` | `int` (rows) | INSERT / UPDATE / DELETE / DDL |
| `ExecuteScalar` | `object?` | Single value, manual cast needed |
| `ExecuteScalarTyped<T>` | `T` | Single value with auto-cast (Guid, DateTime, DateTimeOffset, DateOnly) |
| `ExecuteReader` | `int` (rows) | Streaming processing, many rows |
| `ExecuteReaderList<T>` | `List<T>` | Small result, collect everything |
| `ExecuteReaderFirst<T>` | `T` | First row, first column |
| `BeginBinaryImport` | `ulong` (rows) | Mass import via COPY BINARY |

---

## License

MIT
