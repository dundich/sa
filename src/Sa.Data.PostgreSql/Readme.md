# Sa.Data.PostgreSql

Lightweight Npgsql wrapper for common PostgreSQL operations — no ORM overhead, with DI, Native AOT support, and minimal allocations.

---

## Quick Start

```csharp
// Option 1: direct creation
var dataSource = IPgDataSource.Create("Host=db;Database=mydb;Username=usr;Password=pwd");

// Option 2: via DI
services.AddSaPostgreSqlDataSource(b => b.WithConnectionString("Host=db;Database=mydb;Username=usr;Password=pwd"));
// or with factory (e.g., from IConfiguration):
services.AddSaPostgreSqlDataSource(b => b.WithConnectionString(sp =>
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")));
```

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

Returns the first value from the first column of the first row. Returns `default(T)` if the result is empty.

Supported types: `int`, `long`, `short`, `bool`, `double`, `decimal`, `char`, `string`, `DateTime`, `Guid`, `DateTimeOffset`.

```csharp
// Returns 0 if the table is empty
int errorCount = await dataSource.ExecuteReaderFirst<int>(
    "SELECT COUNT(*) FROM outbox_errors");

// Guid — automatic casting works
Guid firstTenantId = await dataSource.ExecuteReaderFirst<Guid>(
    "SELECT tenant_id FROM tenants LIMIT 1");
```

---

## ExecuteTransactionAsync

Atomic transaction with automatic rollback on error.

```csharp
await dataSource.ExecuteTransactionAsync(async (transaction, ct) =>
{
    // All commands inside use one transaction
    await dataSource.ExecuteNonQuery(
        "INSERT INTO accounts (balance) VALUES (0)", ct);

    await dataSource.ExecuteNonQuery(
        "INSERT INTO transactions (account_id, amount) VALUES (1, 100)", ct);

    // On success — auto-commit
}, IsolationLevel.ReadCommitted, ct);

// On any exception — auto-rollback
try
{
    await dataSource.ExecuteTransactionAsync(async (tx, ct) =>
    {
        throw new InvalidOperationException("Oops");
    }, ct);
}
catch (InvalidOperationException)
{
    // Transaction rolled back automatically
}
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
| `ExecuteReaderFirst<T>` | `T` | One row, one column |
| `BeginBinaryImport` | `ulong` (rows) | Mass import via COPY BINARY |
| `ExecuteTransactionAsync` | `void` | Atomic operations with rollback |

---

## License

MIT
