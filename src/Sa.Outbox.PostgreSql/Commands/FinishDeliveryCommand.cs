using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.SqlBuilder;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class FinishDeliveryCommand(
    IPgDataSource dataSource,
    SqlOutboxBuilder sqlBuilder) : IFinishDeliveryCommand
{
    private readonly SqlCacheSplitter _sqlCache = new(len => sqlBuilder.SqlFinishDelivery(len));

    public async Task<int> Execute<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        IReadOnlyDictionary<Exception, ErrorInfo> errors,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        if (messages.IsEmpty) return 0;

        int total = 0;

        int startIndex = 0;
        foreach ((string sql, int length) in _sqlCache.GetSql(messages.Length))
        {
            var slice = messages.Slice(startIndex, length);

            startIndex += length;

            total += await dataSource.ExecuteNonQuery(
                sql,
                cmd => FillCommandParameters(cmd, slice.Span, errors, filter),
                cancellationToken);
        }

        return total;
    }

    private static void FillCommandParameters<TMessage>(
        NpgsqlCommand cmd,
        ReadOnlySpan<IOutboxContextOperations<TMessage>> messages,
        IReadOnlyDictionary<Exception, ErrorInfo> errors,
        OutboxMessageFilter filter)
    {
        for (int i = 0; i < messages.Length; i++)
        {
            AddContextParameters(cmd, messages[i], errors, i);
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
