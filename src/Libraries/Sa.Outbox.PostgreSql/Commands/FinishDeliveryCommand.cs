using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.SqlBuilder;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed partial class FinishDeliveryCommand(
    IPgDataSource dataSource,
    SqlOutboxBuilder sqlBuilder,
    ILogger<FinishDeliveryCommand>? logger = null) : IFinishDeliveryCommand
{
    private readonly SqlCacheSplitter _sqlCache = new(len => sqlBuilder.SqlFinishDelivery(len));

    private readonly ILogger _logger = logger ?? NullLogger<FinishDeliveryCommand>.Instance;

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

        // `total` is the rowcount of the UPDATE, not of the preceding INSERT INTO __log$:
        // for a WITH ... UPDATE, PostgreSQL reports the top-level statement's tag. A shortfall
        // therefore means some task rows no longer matched the WHERE — in practice the batch's
        // lock had already expired and been taken by another worker, which rewrote
        // task_transact_id. That is at-least-once doing its job, not a lost message, but it is
        // invisible without this line: DeliveryTenant discards the return value, so a client
        // that stalls past LockDuration re-delivers with no trace anywhere.
        if (total != messages.Length)
        {
            LogStolenBatch(
                _logger,
                filter.ConsumerGroupId,
                filter.TenantId,
                filter.TransactId,
                messages.Length,
                total);
        }

        return total;
    }

    [LoggerMessage(
        EventId = 3008,
        Level = LogLevel.Warning,
        Message = "Finished {Updated} of {Expected} tasks for consumer group '{ConsumerGroup}' (tenant {TenantId}, transaction '{TransactId}'): the difference lost the lock to another worker and will be re-delivered. The consumer is expected to be idempotent.")]
    static partial void LogStolenBatch(
        ILogger logger,
        string consumerGroup,
        int tenantId,
        string transactId,
        int expected,
        int updated);

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
        var lockExpiresOn = (result.CreatedAt + context.PostponeDelay).ToUnixTimeSeconds();
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
