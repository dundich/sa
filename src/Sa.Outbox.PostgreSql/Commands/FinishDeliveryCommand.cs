using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.IdGen;

namespace Sa.Outbox.PostgreSql.Commands;

internal class FinishDeliveryCommand(
    IPgDataSource dataSource
    , SqlOutboxTemplate sqlTemplate
    , IIdGenerator idGenerator
) : IFinishDeliveryCommand
{
    const int IndexParamsCount = 7;
    const int ConstParamsCount = 4;


    private readonly SqlCacheSplitter sqlCache = new(len => sqlTemplate.SqlFinishDelivery(len));

    public async Task<int> Execute<TMessage>(
        IOutboxContext<TMessage>[] outboxMessages,
        IReadOnlyDictionary<Exception, ErrorInfo> errors,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        if (outboxMessages.Length == 0) return 0;

        int total = 0;

        int startIndex = 0;
        foreach ((string sql, int len) in sqlCache.GetSql(outboxMessages.Length))
        {
            var segment = new ArraySegment<IOutboxContext<TMessage>>(outboxMessages, startIndex, len);
            startIndex += len;

            NpgsqlParameter[] parameters = GetSqlParams(segment, errors, filter);
            total += await dataSource.ExecuteNonQuery(sql, parameters, cancellationToken);
        }

        return total;
    }

    private NpgsqlParameter[] GetSqlParams<TMessage>(
        ArraySegment<IOutboxContext<TMessage>> sliceContext,
        IReadOnlyDictionary<Exception, ErrorInfo> errors,
        OutboxMessageFilter filter)
    {
        NpgsqlParameter[] parameters = new NpgsqlParameter[sliceContext.Count * IndexParamsCount + ConstParamsCount];

        int j = 0;
        foreach (IOutboxContext<TMessage> context in sliceContext)
        {
            DateTimeOffset createdAt = context.DeliveryResult.CreatedAt;
            string id = idGenerator.GenId(createdAt);
            string msg = context.DeliveryResult.Message;
            long lockExpiresOn = (createdAt + context.PostponeAt).ToUnixTimeSeconds();

            string errorId = String.Empty;
            Exception? error = context.Exception;
            if (error != null)
            {
                errorId = errors[error].ErrorId.ToString();
                if (string.IsNullOrEmpty(msg))
                {
                    msg = error.Message;
                }
            }
            // (@id_{i},@outbox_id_{i},@error_id_{i},@status_code_{i},@status_message_{i},@lock_expires_on_{i},@created_at_{i}
            parameters[j] = new($"@p{j}", id); j++;
            parameters[j] = new($"@p{j}", context.OutboxId); j++;
            parameters[j] = new($"@p{j}", errorId); j++;
            parameters[j] = new($"@p{j}", context.DeliveryResult.Code); j++;
            parameters[j] = new($"@p{j}", msg); j++;
            parameters[j] = new($"@p{j}", lockExpiresOn); j++;
            parameters[j] = new($"@p{j}", createdAt.ToUnixTimeSeconds()); j++;
        }

        //@tenant AND @part AND @from_date AND @transact_id AND @created_at

        parameters[j++] = new("tnt", filter.TenantId);
        parameters[j++] = new("prt", filter.Part);
        parameters[j++] = new("from_date", filter.FromDate.ToUnixTimeSeconds());
        parameters[j] = new("tid", filter.TransactId);

        return parameters;
    }
}
