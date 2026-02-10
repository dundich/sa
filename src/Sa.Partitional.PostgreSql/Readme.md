# Sa.Partitional.PostgreSql

A library designed for managing table partitioning in PostgreSQL
to improve performance and manageability with large volumes of data.

## Capabilities

- Declaratively describe a time-partitioned table (by day, month, year).
- Define partitions based on lists of keys for strings or numbers.
- Schedule migrations for creating new partitions.
- Schedule the removal of old partitions.
- Manage partitions.

## Features

- Since the maximum length of a table name is 63 characters, it is important to consider the naming when creating partitions.
- All tables have a final time interval section represented by a column of type `int64` in Unix timestamp format (in seconds).
- Old partitions are deleted using `DROP`.

## Configuration Example

```csharp

public static class PartitioningSetup
{
    public static IServiceCollection AddPartitioning(this IServiceCollection services)
    {
        services.AddSaPartitional((sp, builder) =>
        {
            builder.AddSchema("public", schema =>
            {
                // Configure the 'customer' table
                schema.AddTable("customer",
                    "id INT NOT NULL",
                    "country TEXT NOT NULL",
                    "city TEXT NOT NULL"
                )
                // Separate partitions in tables
                .WithPartSeparator("_")
                // Partition by 'country' and 'city' (if PartByRange is not specified, defaults to daily)
                .PartByList("country", "city") 
                // Migration of partitions for each tenant by city
                .AddMigration("RU", ["Moscow", "Samara"])
                .AddMigration("USA", ["Alabama", "New York"])
                .AddMigration("FR", ["Paris", "Lyon", "Bordeaux"]);
            });
        })
        // Schedule for creating new partitions
        .AddPartMigrationSchedule((sp, opts) =>
        {
            opts.AsJob = true;
            opts.ExecutionInterval = TimeSpan.FromHour(12);
            opts.ForwardDays = 2;
        })
        // Schedule for removing old partitions
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

### Migration Result

For the example above, the migration will result in two tables:

`customer` - *data table* 

|id|country|city|created_at|
|--|-------|----|----------|
|||||

 
`customer_$part` - *partition tracking table (fragment)*

|id|root|part_values|part_by|from_date|to_date|
|--|----|-----------|-------|---------|-------|
|public."customer_RU_Samara_y2025m01d08"|public.customer|["s:RU","s:Samara"]|Day|1736294400|1736380800|
|public."customer_RU_Samara_y2025m01d09"|public.customer|["s:RU","s:Samara"]|Day|1736380800|1736467200|
|public."customer_USA_Alabama_y2025m01d08"|public.customer|["s:USA","s:Alabama"]|Day|1736294400|1736380800|


#### Final DDL

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

Used to define intervals for data partitioning—splitting data into parts by days, months, or years.

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
*By default, the `created_at` column is used with daily partitioning.*


## IPartitionManager

Interface for managing partitions in the database.

```csharp
public interface IPartitionManager
{
    /// <summary>
    /// Migrates the existing partitions in the database.
    /// This method may be used to reorganize or update partitions based on the current state of the data.
    /// </summary>
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
    ValueTask<bool> EnsureParts(string tableName, DateTimeOffset date, Classes.StrOrNum[] partValues, CancellationToken cancellationToken = default);
}
```
