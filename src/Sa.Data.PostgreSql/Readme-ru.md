# Sa.Data.PostgreSql

Лёгкая обёртка над Npgsql для типичных операций с PostgreSQL — без ORM overhead, с поддержкой DI, AOT и минимальными аллокациями.

---

## Быстрый старт

```csharp
// Вариант 1: прямое создание
var dataSource = IPgDataSource.Create("Host=db;Database=mydb;Username=usr;Password=pwd");

// Вариант 2: через DI
services.AddSaPostgreSqlDataSource(b => b.WithConnectionString("Host=db;Database=mydb;Username=usr;Password=pwd"));
// или с factory (например, из IConfiguration):
services.AddSaPostgreSqlDataSource(b => b.WithConnectionString(sp =>
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")));
```

---

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

---

## ExecuteScalar / ExecuteScalarTyped

Возвращает первое значение первой строки результата. `ExecuteScalarTyped<T>` автоматически кастует результат, включая поддержку `Guid`, `DateTime`, `DateTimeOffset` и `DateOnly → DateTime`.

```csharp
// object? перегрузка
object? count = await dataSource.ExecuteScalar("SELECT COUNT(*) FROM users");

// Типизированная перегрузка
int count = await dataSource.ExecuteScalarTyped<int>("SELECT COUNT(*) FROM users");
long id = await dataSource.ExecuteScalarTyped<long>("SELECT nextval('users_id_seq')");
Guid tenantId = await dataSource.ExecuteScalarTyped<Guid>("SELECT tenant_uuid FROM tenants LIMIT 1");
```

---

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

---

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

---

## ExecuteReaderFirst

Возвращает первое значение из первого столбца первой строки. Возвращает `default(T)` если результат пуст.

```csharp
string name = await dataSource.ExecuteReaderFirst<string>("SELECT name FROM users WHERE id = 42");
// → "Tom" или default(string) если не найдено
```

Поддерживаемые типы: `int`, `long`, `short`, `bool`, `double`, `decimal`, `char`, `string`, `DateTime`, `Guid`, `DateTimeOffset`.

---

## BeginBinaryImport

Быстрый бинарный импорт через COPY BINARY.

```csharp
ulong imported = await dataSource.BeginBinaryImport(
    "COPY users (name, email) FROM stdin BINARY",
    async (writer, ct) =>
    {
        foreach (var user in users)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(user.Name, ct);
            await writer.WriteAsync(user.Email, ct);
        }
        return await writer.CompleteAsync(ct);
    },
    cancellationToken);

Console.WriteLine($"Imported {imported} rows");
```

---

## PgRetryStrategy

Повторы с jitter для транзитных ошибок Npgsql.

```csharp
using Sa.Data.PostgreSql;

// Автоматически повторяет при транзитных ошибках (сброс соединения, таймаут и т.д.)
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

Оптимизированный API для добавления параметризированных команд с предварительно закэшированными именами параметров (минимальные аллокации).

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

---

## Сравнение методов

| Метод | Возврат | Когда использовать |
|-------|---------|-------------------|
| `ExecuteNonQuery` | `int` (строки) | INSERT / UPDATE / DELETE / DDL |
| `ExecuteScalar` | `object?` | Одиночное значение, нужна ручная casts |
| `ExecuteScalarTyped<T>` | `T` | Одиночное значение с авто-кастомом (Guid, DateTime, DateTimeOffset, DateOnly) |
| `ExecuteReader` | `int` (строки) | Потоковая обработка, много строк |
| `ExecuteReaderList<T>` | `List<T>` | Маленький результат, собрать всё |
| `ExecuteReaderFirst<T>` | `T` | Одна строка, одна колонка |
| `BeginBinaryImport` | `ulong` (строки) | Массовый импорт через COPY BINARY |
| `ExecuteTransactionAsync` | `void` | Атомарные операции с rollback |

---

## Лицензия

MIT
