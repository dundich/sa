namespace Sa.Partitional.PostgreSql.SqlBuilder;


/// <summary>
/// public."_outbox" 
/// -- CREATE INDEX IF NOT EXISTS ix__outbox__payload_type ON public."_outbox" (payload_type);
/// </summary>
internal sealed class SqlRootBuilder(ITableSettings settings)
{
    private readonly Lazy<string> _sql = new(settings.CreateRootSql);
    public string CreateSql() => _sql.Value;
}
