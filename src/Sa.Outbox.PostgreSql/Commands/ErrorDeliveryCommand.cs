using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Extensions;
using Sa.Outbox.PostgreSql.SqlBuilder;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class ErrorDeliveryCommand(
    IPgDataSource dataSource,
    SqlOutboxBuilder sqlTemplate): IErrorDeliveryCommand
{

    private readonly SqlCacheSplitter sqlCache = new(len => sqlTemplate.SqlError(len));

    public async Task<IReadOnlyDictionary<Exception, ErrorInfo>> Execute(
        ReadOnlyMemory<IOutboxContext> messages,
        CancellationToken cancellationToken)
    {
        // rows       — one entry per distinct __error$ row, i.e. what actually gets INSERTed.
        // byException — every message's exception mapped to the row it belongs to, which is the
        //               contract callers use to fill in task.error_id.
        Dictionary<ErrorKey, ErrorRow> rows = new(messages.Length);
        Dictionary<Exception, ErrorInfo> byException = new(messages.Length);

        GroupByException(messages.Span, rows, byException);

        int len = rows.Count;

        if (len == 0) return byException;

        ErrorRow[] errorArray = [.. rows.Values];

        int startIndex = 0;

        foreach ((string sql, int count) in sqlCache.GetSql(len))
        {
            await dataSource.ExecuteNonQuery(sql,
                cmd => Fill(cmd, errorArray, startIndex, count),
                cancellationToken);

            startIndex += count;
        }

        return byException;
    }

    private static void Fill(
        NpgsqlCommand command,
        ErrorRow[] errorArray,
        int start,
        int count)
    {
        int i = 0;

        foreach (ErrorRow row in errorArray.AsSpan(start, count))
        {
            //(error_id, error_type, error_message, error_created_at)
            command.AddParamErrorId(row.Info.ErrorId, i);
            command.AddParamTypeName(row.Info.TypeName, i);
            command.AddParamStatusMessage(GetErrorMessage(row.Exception), i);
            command.AddParamCreatedAt(row.Info.CreatedAt, i);
            i++;
        }
    }

    private static string GetErrorMessage(Exception exception)
    {
        // Compose a compact error description without the stack trace.
        // Format: "FullTypeName: Message" (+ inner exception chain).
        var sb = new System.Text.StringBuilder();
        sb.Append(exception.GetType().FullName ?? exception.GetType().Name);
        sb.Append(": ");
        sb.Append(exception.Message);

        Exception? inner = exception.InnerException;
        while (inner != null)
        {
            sb.Append(" -> ");
            sb.Append(inner.GetType().Name);
            sb.Append(": ");
            sb.Append(inner.Message);
            inner = inner.InnerException;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Splits the batch into the distinct <c>__error$</c> rows to log and the per-exception lookup
    /// callers need to fill in <c>task.error_id</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The primary key of an <c>__error$</c> row is <c>(error_id, error_created_at)</c> — see
    /// <c>SqlTemplate</c> — where <c>error_id</c> is a hash of the compact error message and
    /// <c>error_created_at</c> is truncated to the day. Grouping therefore has to use exactly that
    /// key. <c>ON CONFLICT DO NOTHING</c> arbitrates a row only against rows that already exist; it
    /// does not arbitrate between rows of the same statement, so two entries sharing a key inside one
    /// INSERT still raise a duplicate-key error.
    /// </para>
    /// <para>
    /// Grouping by the exception <em>instance</em> — what <see cref="Dictionary{TKey,TValue}"/> does
    /// by default, since <see cref="Exception"/> does not override equality — missed precisely that
    /// case: two separate instances carrying the same type and message produced the same
    /// <c>error_id</c> twice in one statement.
    /// </para>
    /// <para>
    /// The day is part of the key, so the same error on two different days stays two rows and a
    /// batch spanning midnight loses nothing. Every context still maps to the row it belongs to, so
    /// all of them get their <c>task.error_id</c> filled in.
    /// </para>
    /// </remarks>
    internal static void GroupByException(
        ReadOnlySpan<IOutboxContext> messages,
        Dictionary<ErrorKey, ErrorRow> rows,
        Dictionary<Exception, ErrorInfo> byException)
    {
        foreach (var message in messages)
        {
            if (message.Exception is null) continue;

            ErrorInfo info = new(
                GetErrorMessageHash(message.Exception),
                message.Exception.GetType().Name,
                message.DeliveryResult.CreatedAt.StartOfDay());

            var key = new ErrorKey(info.ErrorId, info.CreatedAt);

            if (!rows.ContainsKey(key))
                rows[key] = new ErrorRow(message.Exception, info);

            byException[message.Exception] = info;
        }
    }

    /// <summary>The <c>__error$</c> primary key: <c>(error_id, error_created_at)</c>.</summary>
    internal readonly record struct ErrorKey(long ErrorId, DateTimeOffset CreatedAt);

    /// <summary>One <c>__error$</c> row: the exception the message text is built from, and the stored values.</summary>
    internal readonly record struct ErrorRow(Exception Exception, ErrorInfo Info);

    internal static long GetErrorMessageHash(Exception exception)
    {
        // Hash the same compact representation used in GetErrorMessage so that
        // identical errors produce the same hash even across deployments.
        return GetErrorMessage(exception).GetMurmurHash3();
    }
}
