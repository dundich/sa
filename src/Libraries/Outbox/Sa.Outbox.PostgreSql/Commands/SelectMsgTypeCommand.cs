using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.SqlBuilder;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class SelectMsgTypeCommand(
    IPgDataSource dataSource,
    SqlOutboxBuilder sql,
    NpgsqlOutboxReader outboxReader) : ISelectMsgTypeCommand
{
    public async Task<IReadOnlyCollection<(long id, string typeName)>> Execute(CancellationToken cancellationToken)
    {
        NpgsqlOutboxReader.TypeReader.Ordinals ordinals = default;
        bool haveOrdinals = false;

        return await dataSource.ExecuteReaderList(sql.SqlSelectType,
            reader =>
            {
                if (!haveOrdinals)
                {
                    ordinals = outboxReader.Type.GetOrdinals(reader);
                    haveOrdinals = true;
                }

                return (id: outboxReader.Type.GetTypeId(reader, in ordinals),
                        typeName: outboxReader.Type.GetTypeName(reader, in ordinals));
            }
        , cancellationToken);
    }
}
