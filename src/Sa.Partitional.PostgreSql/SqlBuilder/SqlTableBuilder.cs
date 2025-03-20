using Sa.Classes;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

internal class SqlTableBuilder(ITableSettings settings) : ISqlTableBuilder
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

    public string GetPartsSql(StrOrNum[] partValues) => settings.SelectPartsQualifiedTablesSql(partValues);

    public override string ToString() => $"{FullName} {Settings.PartByListFieldNames}";

    public string SelectPartsFromDate { get; } = settings.SelectPartsFromDateSql();

    public string SelectPartsToDate { get; } = settings.SelectPartsToDateSql();
}
