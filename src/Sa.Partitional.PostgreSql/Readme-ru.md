# Sa.Partitional.PostgreSql

Библиотека декларативного партиционирования таблиц PostgreSQL для .NET 10 — поддерживает **range** (день / месяц / год) и **list** партиционирование с автоматической миграцией, планировщиком очистки и in-memory кэшированием.

---

## Обзор

Большие таблицы PostgreSQL теряют производительность по мере роста. Эта библиотека автоматизирует полный жизненный цикл партиционирования:

1. **Объявление** партиционируемых таблиц через fluent builder.
2. **Миграция** — автоматическое создание отсутствующих партиций перед поступлением данных.
3. **Кэширование** — хранение метаданных партиций в памяти для избежания повторных запросов к каталогу.
4. **Очистка** — удаление старых партиций за пределами настраиваемого окна удержания.

Всё подключается в ASP.NET Core `IServiceCollection` через единственный метод-расширение.

---

## Быстрый старт

```csharp
builder.Services.AddSaPartitional((sp, builder) =>
{
    builder.AddSchema("public", schema =>
    {
        // Таблица с range-партиционированием (по умолчанию ежедневно)
        schema.CreateTable("events")
            .PartByRange(PgPartBy.Day)
            .WithFillFactor(90);
    });
})
// Предварительное создание будущих партиций как фоновая задача
.AddPartMigrationSchedule((sp, opts) => opts.AsBackgroundJob = true)
// Удаление партиций старше 30 дней
.AddPartCleanupSchedule((sp, opts) => opts.AsBackgroundJob = true);
```

---

## Поддерживаемые стратегии

| Стратегия | Описание | Пример |
|-----------|----------|--------|
| **Range** | Партиционирование по временным интервалам — день, месяц или год | `events_y2026m06d26`, `events_y2026m07` |
| **List** | Партиционирование по дискретным значениям ключей (строки или числа) | `orders_RU`, `orders_USA` |

Обе стратегии можно комбинировать иерархически: root-таблица с list-партиционированием может иметь range-разделённых детей.

---

## Документация

| Документ | Содержимое |
|----------|-----------|
| [Guide](Guide.md) | Конфигурация, fluent builder, StrOrNum, соглашения об именовании, примеры DDL |
| [API Reference](ApiReference.md) | Сигнатуры интерфейсов, диаграмма архитектуры, ключевые типы |

---

## Ключевые типы

### IPartitionManager

Главная точка входа для программного управления партициями:

```csharp
public interface IPartitionManager
{
    Task<int> Migrate(CancellationToken ct = default);
    Task<int> Migrate(DateTimeOffset[] dates, CancellationToken ct = default);
    Task<bool> EnsureParts(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken ct = default);
}
```

- `Migrate()` — предварительное создание всех отсутствующих партиций на сегодня + окно вперёд
- `Migrate(dates[])` — предварительное создание только для конкретных дат
- `EnsureParts()` — гарантировать существование конкретной партиции (создаёт при необходимости)

### PgPartBy

Enum стратегии партиционирования с тремя предопределёнными значениями:

| Значение | Формат имени | Пример | Диапазон |
|----------|-------------|--------|---------|
| `PgPartBy.Day` | `yYYYYmmDD` | `events_y2026m06d26` | StartOfDay → +1 день |
| `PgPartBy.Month` | `yYYYYmm` | `events_y2026m07` | StartOfMonth → +1 месяц |
| `PgPartBy.Year` | `yYYYY` | `events_y2026` | StartOfYear → +1 год |

Дополнительные фабричные методы:
```csharp
PgPartBy.FromRange(PartByRange.Day);   // из PartByRange enum
PgPartBy.FromPartName("root");         // из строки имени партиции
```

### StrOrNum

Discriminated union для ключей list-партиционирования — поддерживает и строковые, и числовые значения:

```csharp
// Implicit conversions
StrOrNum s = "tenant_a";     // → ChoiceStr
StrOrNum n = 42L;            // → ChoiceNum

// Pattern matching
result.Match(
    onChoiceStr: v => Console.WriteLine($"String: {v}"),
    onChoiceNum: v => Console.WriteLine($"Number: {v}")
);

// Форматирование
s.ToFmtString();             // "s:tenant_a"
StrOrNum.FromFmtStr("n:42"); // → ChoiceNum(42)
```

Поддерживаемые implicit conversions: `string`, `int`, `long`, `short`.

---

## Fluent Builder API

Регистрация: `Setup.AddSaPartitional()` возвращает `IPartConfiguration`.

```csharp
services.AddSaPartitional((sp, builder) =>
{
    builder.AddSchema("outbox", schema =>
    {
        schema.CreateTable("messages")
            .AddFields("tenant_id varchar(50) NOT NULL")
            .PartByList("tenant_id")
            .WithFillFactor(80)
            .AddMigration("tenant_a", new StrOrNum[] { "tenant_a_1", "tenant_a_2" })
            .AddMigration(new[] { "tenant_b" });
    });
});
```

### Методы ITableBuilder

| Метод | Описание |
|-------|----------|
| `AddFields(params string[])` | Определения колонок (например, `"tenant_id varchar(50) NOT NULL"`) |
| `PartByRange(PgPartBy, fieldName?)` | Range-стратегия (Day/Month/Year) |
| `PartByList(params string[])` | List-партиционирование по колонке(ам) |
| `TimestampAs(fieldName)` | Переопределение имени timestamp-колонки (по умолч.: `created_at`) |
| `WithPartSeparator(string)` | Разделитель между частями в именах (по умолч.: `"__"`) |
| `WithFillFactor(int)` | Параметр хранения PostgreSQL fill factor |
| `WithPartTablePostfix(string)` | Суффикс для кэш-/партиционных таблиц (по умолч.: `"__part"`) |
| `AddPostSql(Func<string>)` | Дополнительный SQL после CREATE TABLE |
| `AddConstraintPkSql(Func<string>)` | Пользовательский CHECK / PK constraint SQL |
| `AddMigration(IPartTableMigrationSupport)` | Пользовательские значения миграции |
| `AddMigration(Func<CancellationToken, Task<StrOrNum[][]>>)` | Асинхронная фабрика значений миграции |
| `AddMigration(params StrOrNum[])` | Inline значения list-партиций |
| `AddMigration(StrOrNum parent, StrOrNum[] childs)` | Иерархическая миграция (parent + дети) |
| `Build()` | Финализация настроек таблицы |

---

## Настройки планировщика

### MigrationScheduleSettings

Управляет автоматическим предварительным созданием будущих партиций:

| Свойство | По умолчанию | Описание |
|----------|-------------|----------|
| `ForwardDays` | `2` | Дней вперёд для пре-создания партиций |
| `AsBackgroundJob` | `false` | Запуск как hosted service |
| `MigrationJobName` | `"Migration job"` | Идентификатор задачи |
| `ExecutionInterval` | `~4h + jitter` | Интервал между миграциями |
| `WaitMigrationTimeout` | `3 сек` | Таймаут ожидания semaphore |

### PartCleanupScheduleSettings

Управляет автоматическим удалением старых партиций:

| Свойство | По умолчанию | Описание |
|----------|-------------|----------|
| `DropPartsAfterRetention` | `30 дней` | Порог возраста для удаления |
| `AsBackgroundJob` | `false` | Запуск как hosted service |
| `ExecutionInterval` | `~4h + jitter` | Интервал между очистками |
| `InitialDelay` | `1 мин` | Задержка перед первым запуском |

### PartCacheSettings

In-memory кэш метаданных партиций:

| Свойство | По умолчанию | Описание |
|----------|-------------|----------|
| `CachedFromDate` | `1 день` | Насколько далеко вперёд загружать партиции |

---

## Соглашения об именовании

Партиции следуют предсказуемым паттернам именования (разделитель по умолчанию `"__"`):

| Компонент | Паттерн | Пример |
|-----------|---------|--------|
| Range (день) | `{table}{sep}y{YYYY}m{MM}d{DD}` | `events__part__y2026m06d26` |
| Range (месяц) | `{table}{sep}y{YYYY}m{MM}` | `events__part__y2026m07` |
| Range (год) | `{table}{sep}y{YYYY}` | `events__part__y2026` |
| List (вложенный) | `{table}{sep}{val1}_{val2}...` | `orders__part__EU_EU_1` |
| Кэш-таблица | `{table}{postfix}` | `events__part` |

**Ограничения:**
- Идентификаторы не должны превышать 63 символа (лимит PostgreSQL).
- Схемы автоматически создаются через `CREATE SCHEMA IF NOT EXISTS`.
- Детские партиции используют `PARTITION OF parent FOR VALUES FROM (...) TO (...)` (range) или `FOR VALUES IN (...)` (list).
- После создания каждой range-партиции кэш-таблица отслеживает границы через `INSERT ... ON CONFLICT (id) DO NOTHING`.

---

## Архитектура

```
┌─────────────────────┐
│  IPartitionManager  │  ← Публичная точка входа
├─────────────────────┤
│  IMigrationService  │  ← Пре-создание будущих партиций
│  IPartCleanupService│  ← Удаление старых партиций
├─────────────────────┤
│  IPartRepository    │  ← Выполнение DDL (CREATE/DROP PARTITION)
│  ISqlBuilder        │  ← Генерация SQL-шаблонов
│  IPartCache         │  ← In-memory кэш метаданных
├─────────────────────┤
│  MigrationJob       │  ← IJob обёртка для Sa.Schedule
│  PartCleanupJob     │  ← IJob обёртка для Sa.Schedule
└─────────────────────┘
```

---

## Зависимости

- `Sa.Data.PostgreSql` — обёртка Npgsql со стратегией повторов
- `Sa.Schedule` — инфраструктура фоновых задач

---

## Структура проекта

```
src/Sa.Partitional.PostgreSql/
├── Setup.cs                        # Главная DI-точка AddSaPartitional()
├── IPartitionManager.cs            # Публичный API управления партициями
├── PgPartBy.cs                     # Enum стратегии партиционирования
├── Classes/
│   ├── StrOrNum.cs                 # Discriminated union (string | long)
│   └── Enumeration.cs              # Шаблон type-safe base enum
├── Configuration/                  # Fluent builder API
│   ├── IPartConfiguration.cs
│   └── Builder/                    # ISettingsBuilder, ISchemaBuilder, ITableBuilder
├── Settings/                       # ITableSettings, ITableSettingsStorage
├── Cache/                          # In-memory кэш: PartCache, PartCacheSettings
├── Migration/                      # Пре-создание: IMigrationService, MigrationJob
├── Cleaning/                       # Удаление старых: IPartCleanupService, PartCleanupJob
├── Partitional/                    # DDL repo: IPartRepository, PartByRangeInfo
└── SqlBuilder/                     # SQL-шаблоны: ISqlBuilder, SqlTemplate.cs
```

---

## Лицензия

MIT
