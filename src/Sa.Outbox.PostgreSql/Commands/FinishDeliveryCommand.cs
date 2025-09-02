﻿using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.IdGen;

namespace Sa.Outbox.PostgreSql.Commands;

internal class FinishDeliveryCommand(IPgDataSource dataSource, SqlOutboxTemplate sqlTemplate, IIdGenerator idGenerator)
    : IFinishDeliveryCommand
{
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
        foreach ((string sql, int length) in sqlCache.GetSql(outboxMessages.Length, CachedSqlParamNames.MaxIndex))
        {
            var slice = new ArraySegment<IOutboxContext<TMessage>>(outboxMessages, startIndex, length);
            startIndex += length;

            total += await dataSource.ExecuteNonQuery(
                sql,
                cmd => FllCmdParams(cmd, slice, errors, filter)
                , cancellationToken);
        }

        return total;
    }

    private void FllCmdParams<TMessage>(
        NpgsqlCommand cmd,
        ArraySegment<IOutboxContext<TMessage>> contexts,
        IReadOnlyDictionary<Exception, ErrorInfo> errors,
        OutboxMessageFilter filter)
    {
        for (int i = 0; i < contexts.Count; i++)
        {
            var context = contexts[i];
            var result = context.DeliveryResult;
            var statusCode = result.Code;
            var createdAt = result.CreatedAt;
            var id = idGenerator.GenId(createdAt);
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
                .AddParameter<CachedSqlParamNames>("@id_", i, id)
                .AddParameter<CachedSqlParamNames>("@oid_", i, context.OutboxId)
                .AddParameter<CachedSqlParamNames>("@err_", i, errorId ?? string.Empty)
                .AddParameter<CachedSqlParamNames>("@st_", i, statusCode)
                .AddParameter<CachedSqlParamNames>("@msg_", i, msg ?? string.Empty)
                .AddParameter<CachedSqlParamNames>("@exp_", i, lockExpiresOn)
                .AddParameter<CachedSqlParamNames>("@cr_", i, createdAt.ToUnixTimeSeconds())
                ;
        }

        cmd.Parameters.Add(new("@tnt", filter.TenantId));
        cmd.Parameters.Add(new("@prt", filter.Part));
        cmd.Parameters.Add(new("@from", filter.FromDate.ToUnixTimeSeconds()));
        cmd.Parameters.Add(new("@tid", filter.TransactId));
    }
}
