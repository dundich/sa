using Npgsql;
using Sa.Data.PostgreSql;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class FinishDeliveryCommand(
    IPgDataSource dataSource,
    SqlOutboxTemplate sqlTemplate) : IFinishDeliveryCommand
{
    private readonly SqlCacheSplitter sqlCache = new(len => sqlTemplate.SqlFinishDelivery(len));

    public async Task<int> Execute(
        IOutboxContext[] outboxMessages,
        IReadOnlyDictionary<Exception, ErrorInfo> errors,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        if (outboxMessages.Length == 0) return 0;

        int total = 0;

        int startIndex = 0;
        foreach ((string sql, int length) in sqlCache.GetSql(outboxMessages.Length, SqlParamNames.MaxIndex))
        {
            var slice = new ArraySegment<IOutboxContext>(outboxMessages, startIndex, length);
            startIndex += length;

            total += await dataSource.ExecuteNonQuery(
                sql,
                cmd => FllCmdParams(cmd, slice, errors, filter),
                cancellationToken);
        }

        return total;
    }

    private static void FllCmdParams(
        NpgsqlCommand cmd,
        ArraySegment<IOutboxContext> contexts,
        IReadOnlyDictionary<Exception, ErrorInfo> errors,
        OutboxMessageFilter filter)
    {
        for (int i = 0; i < contexts.Count; i++)
        {
            var context = contexts[i];
            var result = context.DeliveryResult;
            var statusCode = result.Code;
            var createdAt = result.CreatedAt;

            var msg = result.Message;
            var lockExpiresOn = (createdAt + context.PostponeAt).ToUnixTimeSeconds();

            string? errorId = null;
            Exception? ex = context.Exception;
            if (ex != null && errors.TryGetValue(ex, out var errorInfo))
            {
                errorId = errorInfo.ErrorId.ToString();
                if (string.IsNullOrEmpty(msg))
                {
                    msg = ex.Message;
                }
            }

            cmd

                .AddParameter<SqlParamNames>(SqlParam.StatusCode, i, statusCode)
                .AddParameter<SqlParamNames>(SqlParam.StatusMessage, i, msg ?? string.Empty)
                .AddParameter<SqlParamNames>(SqlParam.CreatedAt, i, createdAt.ToUnixTimeSeconds())

                .AddParameter<SqlParamNames>(SqlParam.PayloadId, i, context.PayloadId)

                .AddParameter<SqlParamNames>(SqlParam.TaskId, i, context.DeliveryInfo.TaskId)

                .AddParameter<SqlParamNames>(SqlParam.LockExpiresOn, i, lockExpiresOn)
                .AddParameter<SqlParamNames>(SqlParam.TaskCreatedAt, i, context.DeliveryInfo.TaskId)

                .AddParameter<SqlParamNames>(SqlParam.ErrorId, i, errorId ?? string.Empty)

                ;
        }

        cmd.Parameters.Add(new(SqlParam.TenantId, filter.TenantId));
        cmd.Parameters.Add(new(SqlParam.ConsumerGroupId, filter.ConsumerGroupId));
        cmd.Parameters.Add(new(SqlParam.FromDate, filter.FromDate.ToUnixTimeSeconds()));
        cmd.Parameters.Add(new(SqlParam.TransactId, filter.TransactId));
    }


    sealed class SqlParamNames : INamePrefixProvider
    {
        public static int MaxIndex => 512;

        public static string[] GetPrefixes() =>
        [
            SqlParam.StatusCode
            , SqlParam.StatusMessage
            , SqlParam.CreatedAt
            , SqlParam.PayloadId
            , SqlParam.TaskId
            , SqlParam.LockExpiresOn
            , SqlParam.TaskCreatedAt
            , SqlParam.ErrorId
        ];
    }
}
