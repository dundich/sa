using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Npgsql;

namespace Sa.Data.PostgreSql;

public static class DbCommandExtensions
{
    /// <summary>
    /// Добавляет параметр с именем {prefix}{index}, используя минимальные аллокации.
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


    public static NpgsqlCommand AddParam<TProvider, T>(
        this NpgsqlCommand command,
        string prefix,
        int index,
        T value)
        where TProvider : INamePrefixProvider
    {
        var paramName = CachedParamNames<TProvider>.Default.Get(prefix, index);
        var param = new NpgsqlParameter<T>(paramName, value);
        command.Parameters.Add(param);
        return command;
    }
}

public interface INamePrefixProvider
{
    static abstract string[] GetPrefixes();
    static abstract int MaxIndex { get; }
}



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

        return "{prefix}{index}";
    }

    private static string[][] CreateCachedArrays(string[] prefixes, int maxCapacity)
    {
        var result = new string[prefixes.Length][];

        for (int i = 0; i < prefixes.Length; i++)
        {
            string prefix = prefixes[i];
            var arr = new string[maxCapacity];
            for (int j = 0; j < maxCapacity; j++)
                arr[j] = $"{prefix}{j}";
            result[i] = arr;
        }

        return result;
    }


    public static readonly CachedParamNames<T> Default = new(T.MaxIndex);
}
