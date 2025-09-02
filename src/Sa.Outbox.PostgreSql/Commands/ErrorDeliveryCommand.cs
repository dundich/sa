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
        Dictionary<Exception, ErrorInfo> errors = GroupByException(outboxMessages);

        int len = errors.Count;

        if (len == 0) return errors;

        KeyValuePair<Exception, ErrorInfo>[] errorArray = [.. errors];

        int startIndex = 0;

        foreach ((string sql, int count) in sqlCache.GetSql(len))
        {
            var slice = errorArray.AsSpan(startIndex, count);
            startIndex += count;

            List<NpgsqlParameter> parameters = [];

            int i = 0;
            // (@id_{i},@type_{i},@message_{i},@created_at_{i}
            foreach ((Exception Key, ErrorInfo Value) in slice)
            {
                (long ErrorId, string TypeName, DateTimeOffset CreatedAt) = Value;

                parameters.Add(new(CachedParamNames.Get("@id_", i), ErrorId));
                parameters.Add(new(CachedParamNames.Get("@type_", i), TypeName));
                parameters.Add(new(CachedParamNames.Get("@message_", i), Key.ToString()));
                parameters.Add(new(CachedParamNames.Get("@created_at_", i), CreatedAt.ToUnixTimeSeconds()));
                i++;
            }

            await dataSource.ExecuteNonQuery(sql, [.. parameters], cancellationToken);
        }

        return errors;
    }

    private static Dictionary<Exception, ErrorInfo> GroupByException(IOutboxContext[] outboxMessages)
    {
        return outboxMessages
              .Where(m => m.Exception != null)
              .GroupBy(m => m.Exception!)
              .Select(m => (err: m.Key, createdAt: m.First().DeliveryResult.CreatedAt.StartOfDay()))
              .ToDictionary(e => e.err, e => new ErrorInfo(e.err.ToString().GetMurmurHash3(), e.err.GetType().Name, e.createdAt));
    }

    private static class CachedParamNames
    {
        public const int MaxIndex = 256;

        private static readonly string[][] IdNames = [
            CreateArray("@id_", MaxIndex),
            CreateArray("@type_", MaxIndex),
            CreateArray("@message_", MaxIndex),
            CreateArray("@created_at_", MaxIndex)
        ];

        private static readonly string[][] s_names = IdNames;

        public static string Get(string prefix, int i)
        {
            // Для малых значений i — кэшируем строки
            if (i < MaxIndex)
                return GetCachedName(prefix, i);

            // На случай очень больших batch — выделяем временно
            return $"{prefix}{i}";
        }

        private static string GetCachedName(string prefix, int i)
        {
            return prefix switch
            {
                "@id_" => s_names[0][i],
                "@type_" => s_names[1][i],
                "@message_" => s_names[2][i],
                "@created_at_" => s_names[3][i],
                _ => throw new ArgumentException("Unsupported prefix")
            };
        }

        private static string[] CreateArray(string prefix, int count)
        {
            var arr = new string[count];
            for (int i = 0; i < count; i++)
                arr[i] = $"{prefix}{i}";
            return arr;
        }
    }
}
