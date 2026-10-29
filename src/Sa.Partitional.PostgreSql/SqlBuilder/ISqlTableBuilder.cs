using Sa.Partitional.PostgreSql.Classes;

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
    string CreateSql(DateTimeOffset date, params StrOrNum[] partValues);

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
