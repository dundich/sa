using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Extensions;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class ErrorDeliveryCommand(IPgDataSource dataSource, SqlOutboxTemplate sqlTemplate)
    : IErrorDeliveryCommand
{

    private readonly SqlCacheSplitter sqlCache = new(len => sqlTemplate.SqlError(len));

    public async Task<IReadOnlyDictionary<Exception, ErrorInfo>> Execute(
        ReadOnlyMemory<IOutboxContext> outboxMessages,
        CancellationToken cancellationToken)
    {
        Dictionary<Exception, ErrorInfo> errors = GroupByException(outboxMessages.Span);

        int len = errors.Count;

        if (len == 0) return errors;

        KeyValuePair<Exception, ErrorInfo>[] errorArray = [.. errors];

        int startIndex = 0;

        foreach ((string sql, int count) in sqlCache.GetSql(len))
        {
            await dataSource.ExecuteNonQuery(sql,
                cmd => Fill(cmd, errorArray, startIndex, count),
                cancellationToken);

            startIndex += count;
        }

        return errors;
    }

    private static void Fill(
        NpgsqlCommand command,
        KeyValuePair<Exception, ErrorInfo>[] errorArray,
        int start,
        int count)
    {
        int i = 0;

        foreach ((Exception Ex, ErrorInfo Value) in errorArray.AsSpan(start, count))
        {

            (long ErrorId, string TypeName, DateTimeOffset CreatedAt) = Value;

            //(error_id, error_type, error_message, error_created_at)
            command.AddParamErrorId(ErrorId, i);
            command.AddParamTypeName(TypeName, i);
            command.AddParamStatusMessage(Ex.ToString(), i);
            command.AddParamCreatedAt(CreatedAt, i);
            i++;
        }
    }

    private static Dictionary<Exception, ErrorInfo> GroupByException(ReadOnlySpan<IOutboxContext> outboxMessages)
    {
        Dictionary<Exception, ErrorInfo> result = new(outboxMessages.Length);

        foreach (var message in outboxMessages)
        {
            if (message.Exception == null || !result.ContainsKey(message.Exception)) break;

            result[message.Exception]
                = new ErrorInfo(
                    message.Exception.ToString().GetMurmurHash3(),
                    message.Exception.GetType().Name,
                    message.DeliveryResult.CreatedAt.StartOfDay())
                ;
        }

        return result;
    }
}
