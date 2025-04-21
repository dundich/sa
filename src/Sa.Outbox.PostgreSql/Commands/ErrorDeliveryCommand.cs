using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Extensions;

namespace Sa.Outbox.PostgreSql.Commands;

internal class ErrorDeliveryCommand(
    IPgDataSource dataSource
    , SqlOutboxTemplate sqlTemplate
) : IErrorDeliveryCommand
{

    private readonly SqlCacheSplitter sqlCache = new(len => sqlTemplate.SqlError(len));

    public async Task<IReadOnlyDictionary<Exception, ErrorInfo>> Execute(IOutboxContext[] outboxMessages, CancellationToken cancellationToken)
    {
        Dictionary<Exception, ErrorInfo> errors = outboxMessages
              .Where(m => m.Exception != null)
              .GroupBy(m => m.Exception!)
              .Select(m => (err: m.Key, createdAt: m.First().DeliveryResult.CreatedAt.StartOfDay()))
              .ToDictionary(e => e.err, e => new ErrorInfo(e.err.ToString().GetMurmurHash3(), e.err.GetType().Name, e.createdAt));

        int len = errors.Count;

        if (len == 0) return errors;

        KeyValuePair<Exception, ErrorInfo>[] arrErrors = [.. errors];

        int startIndex = 0;

        foreach ((string sql, int cnt) in sqlCache.GetSql(len))
        {

            var sliceErrors = new ArraySegment<KeyValuePair<Exception, ErrorInfo>>(arrErrors, startIndex, cnt);

            startIndex += cnt;

            List<NpgsqlParameter> parameters = [];

            int i = 0;
            // (@id_{i},@type_{i},@message_{i},@created_at_{i}
            foreach ((Exception Key, ErrorInfo Value) in sliceErrors)
            {
                (long ErrorId, string TypeName, DateTimeOffset CreatedAt) = Value;

                parameters.Add(new($"@id_{i}", ErrorId));
                parameters.Add(new($"@type_{i}", TypeName));
                parameters.Add(new($"@message_{i}", Key.ToString()));
                parameters.Add(new($"@created_at_{i}", CreatedAt.ToUnixTimeSeconds()));
                i++;
            }

            await dataSource.ExecuteNonQuery(sql, [.. parameters], cancellationToken);
        }

        return errors;
    }
}
