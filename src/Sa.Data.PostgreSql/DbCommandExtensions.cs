using Npgsql;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace Sa.Data.PostgreSql;

public static class DbCommandExtensions
{
    /// <summary>
    /// Adds a parameter with name {prefix}{index}, using minimal allocations.
    /// </summary>
    public static NpgsqlCommand AddParameter<TProvider>(
        this NpgsqlCommand command,
        string prefix,
        int index,
        object? value)
        where TProvider : INamePrefixProvider
    {
        var paramName = CachedParamNames<TProvider>.Default.Get(prefix, index);

        var param = command.CreateParameter();
        param.ParameterName = paramName;
        param.Value = value ?? DBNull.Value;
        command.Parameters.Add(param);

        return command;
    }

    /// <summary>
    /// Adds a parameter — infers the value type from the argument.
    /// </summary>
    public static NpgsqlCommand AddParam<TProvider, T>(
        this NpgsqlCommand command,
        string prefix,
        T? value,
        int index)
        where TProvider : INamePrefixProvider
    {
        var paramName = CachedParamNames<TProvider>.Default.Get(prefix, index);
        var param = command.CreateParameter();
        param.ParameterName = paramName;
        param.Value = value is null ? DBNull.Value : (object)value!;
        command.Parameters.Add(param);

        return command;
    }
}

public interface INamePrefixProvider
{
    static abstract string[] GetPrefixes();
    static abstract int MaxIndex { get; }
}



/// <summary>
/// Pre-allocates parameter-name strings "{prefix}{index}" for every prefix and every index
/// in [0, maxIndex) so the hot path never interpolates. The eager cost is bounded and small:
/// for the outbox bulk provider (BatchParams) that is 9 prefixes × MaxIndex — 9216 strings at
/// MaxIndex = 1024. An index at or above MaxIndex still resolves, via the <see cref="Combine"/>
/// fallback, which is exactly why a provider's MaxIndex and the batch chunk ceiling must stay
/// in lock-step (see Sa.Outbox.PostgreSql.Commands.SqlCacheSplitter.DefaultMaxLen — the two
/// share the same constant). One instance per provider is created lazily and shared for the
/// process lifetime.
/// </summary>
sealed class CachedParamNames<T>(int maxIndex) where T : INamePrefixProvider
{
    private static readonly ReadOnlyDictionary<string, int> PrefixToIndex =
        new(new Dictionary<string, int>(
            T.GetPrefixes().Select((prefix, i) => new KeyValuePair<string, int>(prefix, i))
        ));

    private readonly string[][] s_cachedBuffers = CreateCachedArrays(T.GetPrefixes(), T.MaxIndex);


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string Get(string prefix, int index)
    {
        if (index < maxIndex && PrefixToIndex.TryGetValue(prefix, out int arrayIndex))
        {
            return s_cachedBuffers[arrayIndex][index];
        }

        return Combine(prefix, index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Combine(string prefix, int index)
    {
        return $"{prefix}{index}";
    }

    private static string[][] CreateCachedArrays(string[] prefixes, int maxCapacity)
    {
        var result = new string[prefixes.Length][];

        for (int i = 0; i < prefixes.Length; i++)
        {
            string prefix = prefixes[i];
            var arr = new string[maxCapacity];
            for (int j = 0; j < maxCapacity; j++)
                arr[j] = Combine(prefix, j);
            result[i] = arr;
        }

        return result;
    }


    public static readonly CachedParamNames<T> Default = new(T.MaxIndex);
}
