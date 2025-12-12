using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Extensions;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class ErrorDeliveryCommand(IPgDataSource dataSource, SqlOutboxTemplate sqlTemplate)
    : IErrorDeliveryCommand
{

    private readonly SqlCacheSplitter sqlCache = new(len => sqlTemplate.SqlError(len));

    public async Task<IReadOnlyDictionary<Exception, ErrorInfo>> Execute(IOutboxContext[] outboxMessages, CancellationToken cancellationToken)
    {
        Dictionary<Exception, ErrorInfo> errors = GroupByException(outboxMessages);

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

        foreach ((Exception Key, ErrorInfo Value) in errorArray.AsSpan(start, count))
        {
            (long ErrorId, string TypeName, DateTimeOffset CreatedAt) = Value;

            command.AddParameter<SqlParamNames>(SqlParam.ErrorId, i, ErrorId);
            command.AddParameter<SqlParamNames>(SqlParam.TypeName, i, TypeName);
            command.AddParameter<SqlParamNames>(SqlParam.StatusMessage, i, Key.ToString());
            command.AddParameter<SqlParamNames>(SqlParam.CreatedAt, i, CreatedAt.ToUnixTimeSeconds());
            i++;
        }
    }

    private static Dictionary<Exception, ErrorInfo> GroupByException(IOutboxContext[] outboxMessages)
    {
        return outboxMessages
              .Where(m => m.Exception != null)
              .GroupBy(m => m.Exception!)
              .Select(m => (err: m.Key, createdAt: m.First().DeliveryResult.CreatedAt.StartOfDay()))
              .ToDictionary(e => e.err, e => new ErrorInfo(e.err.ToString().GetMurmurHash3(), e.err.GetType().Name, e.createdAt));
    }


    sealed class SqlParamNames : INamePrefixProvider
    {
        public static int MaxIndex => 512;

        public static string[] GetPrefixes() =>
        [
            SqlParam.ErrorId
            , SqlParam.TypeName
            , SqlParam.StatusMessage
            , SqlParam.CreatedAt
        ];
    }
}
