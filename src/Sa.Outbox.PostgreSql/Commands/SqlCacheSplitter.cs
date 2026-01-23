namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class SqlCacheSplitter(Func<int, string> genSql)
{
    private readonly Dictionary<int, string> _sqlCache = [];

    public IEnumerable<(string sql, int length)> GetSql(int len, int maxLen = 512)
    {
        if (len <= 0)
        {
            yield break;
        }

        int multipleOf16 = len / 16 * 16;

        if (multipleOf16 > maxLen)
        {
            int multipleOfMax = multipleOf16 / maxLen;

            for (int i = 0; i < multipleOfMax; i++)
            {
                yield return GetOrAdd(maxLen);
            }
        }
        else if (multipleOf16 > 0)
        {
            yield return GetOrAdd(multipleOf16);
        }

        int diff = len - multipleOf16;

        if (diff > 0)
        {
            yield return GetOrAdd(diff);
        }
    }

    private (string, int) GetOrAdd(int len)
    {
        if (!_sqlCache.TryGetValue(len, out string? sql))
        {
            sql = genSql(len);
            _sqlCache[len] = sql;
        }

        return (sql, len);
    }
}
