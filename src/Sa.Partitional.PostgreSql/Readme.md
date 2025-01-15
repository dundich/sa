# Sa.Partitional.PostgreSql

Библиотека, предназначенная для управления партиционированием таблиц в PostgreSQL
c целью улучшения производительности и управляемости в больших объемах данных.

## Позволяет

- Декларативно описать секционируемую таблицу по времени (день, месяц, год).
- Задать секции по спискам ключей для строк или чисел. 
- Задать расписание миграций для создания новых партиций.
- Задать расписание для удаления старых партиций.
- Управлять партициями.


## Особенности

- Так как макс. длина наименования таблицы составляет 63 символа, то следует учитывать выбор значения при создании партиции.
- Все таблицы имеют финальной секцией интервал по времени, который представлен столбцом с типом `int64` в формате Unix timestamps in seconds.
- Удаление старых партиций производится через DROP.


## Пример конфигурирования

```csharp

public static class PartitioningSetup
{
    public static IServiceCollection AddPartitioning(this IServiceCollection services)
    {
        services.AddPartitional((sp, builder) =>
        {
            builder.AddSchema("public", schema =>
            {
                // Настройка таблицы orders
                schema.AddTable("orders",
                    "id INT NOT NULL",
                    "tenant_id INT NOT NULL",
                    "region TEXT NOT NULL",
                    "amount DECIMAL(10, 2) NOT NULL"
                )
                // Партиционирование по tenant_id и region
                .PartByList("tenant_id", "region") 
                // с интервалом в месяц по заданному столбцу
                .PartByRange(PgPartBy.Month, "created_at")
                ; 


                // Настройка таблицы customer
                schema.AddTable("customer",
                    "id INT NOT NULL",
                    "country TEXT NOT NULL",
                    "city TEXT NOT NULL"
                )
                // разделить в таблицах меж партиций
                .WithPartSeparator("_")
                // Партиционирование по country и city (если не задан PartByRange то по дням)
                .PartByList("country", "city") 
                // Миграция партиций каждого тенанта по city
                .AddMigration("RU", ["Moscow", "Samara"])
                .AddMigration("USA", ["Alabama", "New York"])
                .AddMigration("FR", ["Paris", "Lyon", "Bordeaux"]);
            });
        })
        // расписание миграций - создания новых партиций
        .AddPartMigrationSchedule((sp, opts) =>
        {
            opts.AsJob = true;
            opts.ExecutionInterval = TimeSpan.FromHour(12);
            opts.ForwardDays = 2;
        })
        // расписание удаления старых партиций
        .AddPartCleanupSchedule((sp, opts) =>
        {
            opts.AsJob = true;
            opts.DropPartsAfterRetention = TimeSpan.FromDays(21);
        })
        ;

        return services;
    }
}

```

### Результат миграции

Для примера выше - результатом миграции будут две таблицы:

`customer` - *таблица с данными* 

|id|country|city|created_at|
|--|-------|----|----------|
|||||

 
`customer_$part` - *таблица для учета партиций (фрагмент)*

|id|root|part_values|part_by|from_date|to_date|
|--|----|-----------|-------|---------|-------|
|public."customer_RU_Samara_y2025m01d08"|public.customer|["s:RU","s:Samara"]|Day|1736294400|1736380800|
|public."customer_RU_Samara_y2025m01d09"|public.customer|["s:RU","s:Samara"]|Day|1736380800|1736467200|
|public."customer_USA_Alabama_y2025m01d08"|public.customer|["s:USA","s:Alabama"]|Day|1736294400|1736380800|


#### Итоговый DDL

```sql

CREATE TABLE public."customer_$part" (
	id text NOT NULL,
	root text NOT NULL,
	part_values text NOT NULL,
	part_by text NOT NULL,
	from_date int8 NOT NULL,
	to_date int8 NOT NULL,
	CONSTRAINT "customer_$part_pkey" PRIMARY KEY (id)
);


CREATE TABLE public.customer (
	id int4 NOT NULL,
	country text NOT NULL,
	city text NOT NULL,
	created_at int8 NOT NULL,
	CONSTRAINT pk_customer PRIMARY KEY (id, country, city, created_at)
)
PARTITION BY LIST (country);

-- Partitions

CREATE TABLE public."customer_FR" PARTITION OF public.customer FOR VALUES IN ('FR')
PARTITION BY LIST (city);

-- Partitions

CREATE TABLE public."customer_FR_Bordeaux" PARTITION OF public."customer_FR" FOR VALUES IN ('Bordeaux')
PARTITION BY RANGE (created_at);

-- Partitions

CREATE TABLE public."customer_FR_Bordeaux_y2025m01d08" PARTITION OF public."customer_FR_Bordeaux"  FOR VALUES FROM ('1736294400') TO ('1736380800');
CREATE TABLE public."customer_FR_Bordeaux_y2025m01d09" PARTITION OF public."customer_FR_Bordeaux"  FOR VALUES FROM ('1736380800') TO ('1736467200');


CREATE TABLE public."customer_FR_Lyon" PARTITION OF public."customer_FR" FOR VALUES IN ('Lyon')
PARTITION BY RANGE (created_at);

-- Partitions

CREATE TABLE public."customer_FR_Lyon_y2025m01d08" PARTITION OF public."customer_FR_Lyon"  FOR VALUES FROM ('1736294400') TO ('1736380800');
CREATE TABLE public."customer_FR_Lyon_y2025m01d09" PARTITION OF public."customer_FR_Lyon"  FOR VALUES FROM ('1736380800') TO ('1736467200');


CREATE TABLE public."customer_FR_Paris" PARTITION OF public."customer_FR" FOR VALUES IN ('Paris')
PARTITION BY RANGE (created_at);

-- Partitions

CREATE TABLE public."customer_FR_Paris_y2025m01d08" PARTITION OF public."customer_FR_Paris"  FOR VALUES FROM ('1736294400') TO ('1736380800');
CREATE TABLE public."customer_FR_Paris_y2025m01d09" PARTITION OF public."customer_FR_Paris"  FOR VALUES FROM ('1736380800') TO ('1736467200');

-- RU

CREATE TABLE public."customer_RU" PARTITION OF public.customer FOR VALUES IN ('RU')
PARTITION BY LIST (city);

CREATE TABLE public."customer_RU_Moscow" PARTITION OF public."customer_RU" FOR VALUES IN ('Moscow')
PARTITION BY RANGE (created_at);

CREATE TABLE public."customer_RU_Moscow_y2025m01d08" PARTITION OF public."customer_RU_Moscow"  FOR VALUES FROM ('1736294400') TO ('1736380800');
CREATE TAB...

-- USA

...
```



## PartByRange 

Используется для обозначения интервалов партиционирования данных - разбиение данных на части по дням, месяцам или годам. 

```csharp
/// <summary>
/// Enumerates the possible partitional ranges for a PostgreSQL database.
/// </summary>
public enum PartByRange
{
    Day,
    Month,
    Year
}
```
*По умолчанию используется столбец `created_at` с разбиением по дням*


## IPartitionManager

Интерфейс для управления партициями в базе данных.

```csharp
public interface IPartitionManager
{
    /// <summary>
    /// Migrates the existing partitions in the database.
    /// This method may be used to reorganize or update partitions based on the current state of the data.
    Task<int> Migrate(CancellationToken cancellationToken = default);

    /// <summary>
    /// Migrates partitions for specific dates.
    /// This method allows for targeted migration of partitions based on the provided date range.
    /// </summary>
    Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures that the specified partitions exist for a given table and date.
    /// This method checks if the specified partitions are present and creates them if they are not.
    /// </summary>
    /// <param name="tableName">The name of the table for which partitions are being ensured.</param>
    /// <param name="date">The date associated with the partition.</param>
    /// <param name="partValues">An array of values that define the partitions (could be strings or numbers).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    ValueTask<bool> EnsureParts(string tableName, DateTimeOffset date, Classes.StrOrNum[] partValues, CancellationToken cancellationToken = default);
}
```