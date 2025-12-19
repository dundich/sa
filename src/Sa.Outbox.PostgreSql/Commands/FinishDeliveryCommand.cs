using Npgsql;
using Sa.Data.PostgreSql;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class FinishDeliveryCommand(
    IPgDataSource dataSource,
    SqlOutboxTemplate sqlTemplate) : IFinishDeliveryCommand
{
    private readonly SqlCacheSplitter _sqlCache = new(len => sqlTemplate.SqlFinishDelivery(len));

    public async Task<int> Execute(
        IOutboxContext[] outboxMessages,
        IReadOnlyDictionary<Exception, ErrorInfo> errors,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        if (outboxMessages.Length == 0) return 0;

        int total = 0;

        int startIndex = 0;
        foreach ((string sql, int length) in _sqlCache.GetSql(outboxMessages.Length))
        {
            var slice = new ArraySegment<IOutboxContext>(outboxMessages, startIndex, length);
            startIndex += length;

            total += await dataSource.ExecuteNonQuery(
                sql,
                cmd => FillCommandParameters(cmd, slice, errors, filter),
                cancellationToken);
        }

        return total;
    }

    private static void FillCommandParameters(
        NpgsqlCommand cmd,
        ArraySegment<IOutboxContext> contexts,
        IReadOnlyDictionary<Exception, ErrorInfo> errors,
        OutboxMessageFilter filter)
    {
        for (int i = 0; i < contexts.Count; i++)
        {
            AddContextParameters(cmd, contexts[i], errors, i);
        }

        cmd
            .AddParamTenantId(filter.TenantId)
            .AddParamConsumerGroupId(filter.ConsumerGroupId)
            .AddParamFromDate(filter.FromDate)
            .AddParamTransactId(filter.TransactId)
            ;
    }


    private static void AddContextParameters(
        NpgsqlCommand cmd,
        IOutboxContext context,
        IReadOnlyDictionary<Exception, ErrorInfo> errors,
        int index)
    {
        var result = context.DeliveryResult;

        var message = GetErrorMessage(context.Exception, result.Message, errors);
        var lockExpiresOn = (result.CreatedAt + context.PostponeAt).ToUnixTimeSeconds();
        var errorId = GetErrorId(context.Exception, errors);

        cmd
            .AddParamStatusCode(result.Code, index)
            .AddParamStatusMessage(message, index)
            .AddParamCreatedAt(result.CreatedAt, index)
            .AddParamTaskCreatedAt(context.DeliveryInfo.PartInfo.CreatedAt, index)
            .AddParamPayloadId(context.PayloadId, index)
            .AddParamTaskId(context.DeliveryInfo.TaskId, index)
            .AddParamLockExpiresOn(lockExpiresOn, index)
            .AddParamErrorId(errorId, index);
    }

    private static long? GetErrorId(Exception? exception, IReadOnlyDictionary<Exception, ErrorInfo> errors)
        => exception != null && errors.TryGetValue(exception, out var errorInfo)
            ? errorInfo.ErrorId
            : null;

    private static string? GetErrorMessage(
        Exception? exception,
        string? currentMessage,
        IReadOnlyDictionary<Exception, ErrorInfo> errors)
    {
        if (!string.IsNullOrEmpty(currentMessage)) return currentMessage;

        return exception != null && errors.TryGetValue(exception, out _)
            ? exception.Message
            : currentMessage;
    }
}
