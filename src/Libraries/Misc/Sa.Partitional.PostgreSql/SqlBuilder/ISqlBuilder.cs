namespace Sa.Partitional.PostgreSql;

/// <summary>
/// One statement batch of a migration, or the failure of one table's migration.
/// </summary>
/// <param name="TableName">The root table the batch (or the failure) belongs to.</param>
/// <param name="Sql">The statement batch to execute, or <c>null</c> for a failure.</param>
/// <param name="Error">
/// The exception that ended this table's migration, or <c>null</c> for a batch.
/// </param>
/// <remarks>
/// A failure is reported per table so the caller can log it and still let the remaining tables of the
/// run create their partitions. The migration as a whole still fails — that is the documented
/// behaviour of the background job — but one broken table no longer starves every other one.
/// </remarks>
internal readonly record struct MigrateBatch(string TableName, string? Sql, Exception? Error);

internal interface ISqlBuilder
{
    ISqlTableBuilder? this[string tableName] { get; }

    IReadOnlyCollection<ISqlTableBuilder> Tables { get; }

    IAsyncEnumerable<MigrateBatch> MigrateSql(DateTimeOffset[] dates, Func<string, Task<StrOrNum[][]>> resolve);
    string SelectPartsFromDateSql(string tableName);

    string SelectPartsToDateSql(string tableName);
}
