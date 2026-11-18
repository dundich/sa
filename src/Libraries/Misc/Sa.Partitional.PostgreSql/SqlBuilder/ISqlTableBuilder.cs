namespace Sa.Partitional.PostgreSql;

internal interface ISqlTableBuilder
{
    string FullName { get; }

    ITableSettings Settings { get; }

    /// <summary>
    /// The range strategy of the table, hoisted out of <see cref="Settings"/> so a caller that only
    /// needs the granularity (the partition cache sizes its snapshot window by it) does not have to
    /// pull in the whole settings object.
    /// </summary>
    PgPartBy PartBy { get; }

    string SelectPartsFromDate { get; }
    string SelectPartsToDate { get; }

    /// <summary>
    /// The complete DDL batch of one partition: schema (in <see cref="TableRootMode.Create"/> mode),
    /// root table, cache table, list levels and the range leaf with its cache row.
    /// </summary>
    string CreateSql(DateTimeOffset date, params StrOrNum[] partValues);

    /// <summary>
    /// The DDL batch of one partition <b>without</b> the schema/root/cache-table statements, so a
    /// bulk migration or an adoption pass does not re-emit them for every date and every list value.
    /// </summary>
    /// <remarks>
    /// The root and the cache table have to exist before the first call: migration emits
    /// <see cref="CreateSql"/> for the first batch of each table, and the adoption step runs
    /// <see cref="CreateRootSql"/> and <see cref="CreateCacheSql"/> once before copying the rows.
    /// </remarks>
    string CreatePartSql(DateTimeOffset date, StrOrNum[] partValues);

    /// <summary>
    /// DDL of the partitioned root table (schema, root, primary key, optional DEFAULT partition).
    /// Used by the adoption step, which converts an existing plain table; regular migration emits it
    /// only in <see cref="TableRootMode.Create"/>.
    /// </summary>
    string CreateRootSql();

    /// <summary>
    /// DDL of the partition cache table. The adoption step creates it even for a table with no rows,
    /// because its presence is what marks the table as adopted.
    /// </summary>
    string CreateCacheSql();
}
