using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

internal sealed class SqlBuilder(ITableSettingsStorage storage) : ISqlBuilder
{
    private readonly Dictionary<string, SqlTableBuilder> builders = storage
        .Tables
        .Select(table => new SqlTableBuilder(table))
        .ToDictionary(c => c.FullName);


    public IReadOnlyCollection<ISqlTableBuilder> Tables => builders.Values;

    public async IAsyncEnumerable<string> MigrateSql(DateTimeOffset[] dates, Func<string, Task<StrOrNum[][]>> resolve)
    {
        foreach (string table in builders.Keys)
        {
            SqlTableBuilder builder = builders[table] ?? throw new KeyNotFoundException(table);

            StrOrNum[][] parValues = await resolve(table);

            if (parValues.Length > 0)
            {
                foreach (StrOrNum[] parts in parValues)
                {
                    foreach (DateTimeOffset date in dates)
                    {
                        string sql = builder.CreateSql(date, parts);
                        yield return sql;
                    }
                }
            }
            else
            {
                foreach (DateTimeOffset date in dates)
                {
                    string sql = builder.CreateSql(date);
                    yield return sql;
                }
            }
        }
    }

    public ISqlTableBuilder? this[string tableName] => Find(tableName ?? throw new ArgumentNullException(nameof(tableName)));


    public string SelectPartsQualifiedTablesSql(string tableName, StrOrNum[] partValues)
        => (Find(tableName) ?? throw new KeyNotFoundException(tableName)).GetPartsSql(partValues);

    public string SelectPartsQualifiedTablesSql(string qualifiedTablesSql)
        => SqlTemplate.SelectPartsQualifiedTablesSql(qualifiedTablesSql);

    public string SelectPartsFromDateSql(string tableName)
        => (Find(tableName) ?? throw new KeyNotFoundException(tableName)).SelectPartsFromDate;

    public string SelectPartsToDateSql(string tableName)
        => (Find(tableName) ?? throw new KeyNotFoundException(tableName)).SelectPartsToDate;

    public string CreatePartSql(string tableName, DateTimeOffset date, StrOrNum[] partValues)
        => (Find(tableName) ?? throw new KeyNotFoundException(tableName)).CreateSql(date);

    #region privates
    private ISqlTableBuilder? Find(string tableName)
    {
        ISqlTableBuilder? item =
            (
                storage.Schemas.Count == 1
                    ? builders.GetValueOrDefault(GetFullName(storage.Schemas.First(), tableName))
                    : builders.Values.FirstOrDefault(c => c.FullName == tableName)
            )
            ?? builders.GetValueOrDefault(tableName);

        if (item != null) return item;

        if (tableName.Contains('"'))
        {
            tableName = tableName.Replace("\"", "");
            return Find(tableName);
        }
        return item;
    }

    static string GetFullName(string schemaName, string tableName) => $"{schemaName}.{tableName}";



    #endregion
}
