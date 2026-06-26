using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

internal sealed class SqlTableBuilder(ITableSettings settings) : ISqlTableBuilder
{
    private readonly SqlRootBuilder rootBuilder = new(settings);
    private readonly SqlPartListBuilder partListBuilder = new(settings);
    private readonly SqlPartRangeBuilder partRangeBuilder = new(settings);

    public string FullName => settings.FullName;

    public ITableSettings Settings => settings;


    public string CreateSql(DateTimeOffset date, params StrOrNum[] partValues)
    {
        if (settings.PartByListFieldNames.Length != partValues.Length)
        {
            return
$"""
-- {date}
{rootBuilder.CreateSql()}

-- incomplete number of parts
{partListBuilder.CreateSql(partValues)}
""";

        }

        return
$"""
-- {date}
{rootBuilder.CreateSql()}
{partListBuilder.CreateSql(partValues)}
{partRangeBuilder.CreateSql(date, partValues)}
""";
    }

    /// <summary>
    /// Creates SQL for a table with no LIST partitioning (RANGE only).
    /// </summary>
    public string CreateSql(DateTimeOffset date)
    {
        return
$"""
-- {date}
{rootBuilder.CreateSql()}
{partRangeBuilder.CreateSql(date, [])}
""";
    }

    public string GetPartsSql(StrOrNum[] partValues) => settings.SelectPartsQualifiedTablesSql(partValues);

    public override string ToString() => $"{FullName} {Settings.PartByListFieldNames}";

    public string SelectPartsFromDate { get; } = settings.SelectPartsFromDateSql();

    public string SelectPartsToDate { get; } = settings.SelectPartsToDateSql();
}
