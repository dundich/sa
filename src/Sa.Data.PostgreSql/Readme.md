# Sa.Data.PostgreSql

Лёгкая обёртка над Npgsql для типичных операций с PostgreSQL — без ORM overhead, с поддержкой DI, AOT и минимальными аллокациями.

## Быстрый старт

```csharp
// Вариант 1: прямой создание
var dataSource = IPgDataSource.Create("Host=db;Database=mydb;Username=usr;Password=pwd");

// Вариант 2: через DI
services.AddSaPostgreSqlDataSource(b => b.WithConnectionString("Host=db;Database=mydb;Username=usr;Password=pwd"));
// или с factory (например, из IConfiguration):
services.AddSaPostgreSqlDataSource(b => b.WithConnectionString(sp =>
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")));
```

## ExecuteNonQuery

Выполняет SQL-команду, которая не возвращает данные (INSERT / UPDATE / DELETE / DDL), и возвращает число затронутых строк.

```csharp
// Простой запрос
int affected = await dataSource.ExecuteNonQuery("DELETE FROM sessions WHERE expired = true");

// С параметрами
int affected = await dataSource.ExecuteNonQuery("""
    INSERT INTO users (name, age) VALUES (@p0, @p1);
    """, [
        new NpgsqlParameter { ParameterName = "p0", Value = "Tom" },
        new NpgsqlParameter { ParameterName = "p1", Value = 18 }
    ]);
```

## ExecuteScalar / ExecuteScalarTyped

Возвращает первое значение первой строки результата. `ExecuteScalarTyped<T>` автоматически кастует результат, включая поддержку `Guid`, `DateTime`, `DateTimeOffset` и `DateOnly → DateTime`.

```csharp
// object? overload
object? count = await dataSource.ExecuteScalar("SELECT COUNT(*) FROM users");

// Typed overload
int count = await dataSource.ExecuteScalarTyped<int>("SELECT COUNT(*) FROM users");
long id = await dataSource.ExecuteScalarTyped<long>("SELECT nextval('users_id_seq')");
Guid tenantId = await dataSource.ExecuteScalarTyped<Guid>("SELECT tenant_uuid FROM tenants LIMIT 1");
```

## ExecuteReader

Потоковое чтение строк с callback'ом — идеально для обработки больших результатов без загрузки в память.

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

## ExecuteReaderList

Читает все строки и собирает их в `List<T>`.

```csharp
// Простая проекция
var names = await dataSource.ExecuteReaderList<string>(
    "SELECT name FROM users ORDER BY name",
    reader => reader.GetString(0));

// С параметрами
var activeUsers = await dataSource.ExecuteReaderList<(int Id, string Name)>(
    """SELECT id, name FROM users WHERE active = @active ORDER BY name""",
    reader => (reader.GetInt32(0), reader.GetString(1)),
    [new NpgsqlParameter { ParameterName = "active", Value = true }]);
```

## ExecuteReaderFirst

Возвращает первое значение из первого столбца первой строки. Возвращает `default(T)` если результат пуст.

Поддерживаемые типы: `int`, `long`, `short`, `bool`, `double`, `decimal`, `char`, `string`, `DateTime`, `Guid`, `DateTimeOffset`.

```csharp
// Вернёт 0 если таблица пуста
int errorCount = await dataSource.ExecuteReaderFirst<int>(
    "SELECT COUNT(*) FROM outbox_errors");

// Guid — работает автоматически
Guid firstTenantId = await dataSource.ExecuteReaderFirst<Guid>(
    "SELECT tenant_id FROM tenants LIMIT 1");
```

## ExecuteTransactionAsync

Атомарная транзакция с автоматическим rollback при ошибке.

```csharp
await dataSource.ExecuteTransactionAsync(async (transaction, ct) =>
{
    // Все команды внутри используют одну транзакцию
    await dataSource.ExecuteNonQuery(
        "INSERT INTO accounts (balance) VALUES (0)", ct);

    await dataSource.ExecuteNonQuery(
        "INSERT INTO transactions (account_id, amount) VALUES (1, 100)", ct);

    // При успехе — авто-commit
}, IsolationLevel.ReadCommitted, ct);

// При любом исключении — авто-rollback
try
{
    await dataSource.ExecuteTransactionAsync(async (tx, ct) =>
    {
        throw new InvalidOperationException("Oops");
    }, ct);
}
catch (InvalidOperationException)
{
    // Транзакция откатилась автоматически
}
```

## BeginBinaryImport

Быстрый бинарный импорт данных через COPY — в разы быстрее поштучных INSERT'ов.

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


## PgRetryStrategy

Повтор попыток с jitter для transient-ошибок Npgsql.

```csharp
using Sa.Data.PostgreSql;

// Автоматически повторяет при transient-ошибках (connection reset, timeout и т.п.)
var result = await PgRetryStrategy.ExecuteWithRetry(
    async ct =>
    {
        using var conn = await dataSource.OpenDbConnection(ct);
        return await conn.OpenAsync(ct);
    },
    retryCount: 5,
    initialDelay: 530);
```

## DbCommandExtensions + INamePrefixProvider

Оптимизированный API для добавления параметризованных команд с пред-кэшированными именами параметров (минимальные аллокации).

```csharp
// Объявите провайдер префиксов
public class UserParams : INamePrefixProvider
{
    public static string[] GetPrefixes() => ["name", "age", "email"];
    public static int MaxIndex => 10;
}

// Используйте — имена генерируются как @name0, @name1, ..., @age0, ...
var cmd = new NpgsqlCommand("SELECT * FROM users WHERE name = @name0 AND age > @age0")
    .AddParam<UserParams>("name", "Tom", 0)
    .AddParam<UserParams>("age", 18, 0);
```

## Сравнение методов

| Метод | Возврат | Когда использовать |
|---|---|---|
| `ExecuteNonQuery` | `int` (строки) | INSERT / UPDATE / DELETE / DDL |
| `ExecuteScalar` | `object?` | Одно значение, нужна ручная конвертация |
| `ExecuteScalarTyped<T>` | `T` | Одно значение с авто-кастом (Guid, DateTime, DateTimeOffset, DateOnly) |
| `ExecuteReader` | `int` (строки) | Потоковая обработка, много строк |
| `ExecuteReaderList<T>` | `List<T>` | Небольшой результат, нужно собрать всё |
| `ExecuteReaderFirst<T>` | `T` | Одна строка одного столбца |
| `BeginBinaryImport` | `ulong` (строки) | Массовый импорт COPY BINARY |
| `ExecuteTransactionAsync` | `void` | Атомарные операции с rollback |
