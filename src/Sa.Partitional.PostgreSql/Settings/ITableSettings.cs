namespace Sa.Partitional.PostgreSql;

/// <summary>
/// for managing database table configurations
/// </summary>
public interface ITableSettings
{
    /// <summary>
    /// Gets the full name of the table, including schema.
    /// </summary>
    string FullName { get; }

    /// <summary>
    /// Gets the name of the database schema where the table resides.
    /// </summary>
    string DatabaseSchemaName { get; }

    /// <summary>
    /// Gets the actual name of the table in the database.
    /// </summary>
    string DatabaseTableName { get; }

    /// <summary>
    /// Gets the name of the primary key field for the table.
    /// </summary>
    string IdFieldName { get; }

    /// <summary>
    /// Gets an array of field names that are part of the table.
    /// </summary>
    string[] Fields { get; }

    /// <summary>
    /// Gets an array of field names used for partitioning the table by list.
    /// </summary>
    string[] PartByListFieldNames { get; }

    /// <summary>
    /// Gets the name of the field used for range partitioning.
    /// Typically a date or numeric field.
    /// </summary>
    string PartByRangeFieldName { get; }

    /// <summary>
    /// Gets the type of partitioning being used (e.g., list, range).
    /// </summary>
    PgPartBy PartBy { get; }

    /// <summary>
    /// Gets an instance that supports migration for partitioned tables.
    /// </summary>
    IPartTableMigrationSupport Migration { get; }

    /// <summary>
    /// Gets the SQL separator used in partitioning queries.
    /// </summary>
    string SqlPartSeparator { get; }

    /// <summary>
    /// Gets a function that returns additional SQL to be executed after the root SQL statement.
    /// </summary>
    Func<string>? PostRootSql { get; }

    /// <summary>
    /// Gets a function that returns SQL for defining primary key constraints.
    /// </summary>
    Func<string>? ConstraintPkSql { get; }
    /// <summary>
    /// WITH (fillfactor = ?);
    /// </summary>
    int? FillFactor { get; }
}