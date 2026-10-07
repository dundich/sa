using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

/// <summary>
/// The effective storage-type order used to build a basket's chain from the registered storages.
/// </summary>
/// <remarks>
/// The order is optional: when neither the global list nor the basket override is non-empty,
/// the chain is the registration order and every registered type participates.
/// When an effective order exists:
/// <list type="bullet">
/// <item>a listed type without a registered storage is skipped silently;</item>
/// <item>a registered type that is not listed does not participate in the basket's operations;</item>
/// <item>storages of one type keep their registration order, and duplicate entries in the list
/// are ignored (the list is de-duplicated).</item>
/// </list>
/// </remarks>
internal sealed class StorageTypeOrdering
{
    /// <summary>
    /// No order configured: registration order, all registered types participate.
    /// </summary>
    public static StorageTypeOrdering Empty { get; } = new(null, null);

    private readonly string[]? _globalOrder;
    private readonly Dictionary<string, string[]> _basketOrder;

    public StorageTypeOrdering(
        IEnumerable<string>? globalOrder,
        IReadOnlyDictionary<string, IEnumerable<string>>? basketOrder)
    {
        _globalOrder = Normalize(globalOrder);
        _basketOrder = [];

        if (basketOrder is not null)
        {
            foreach (var (basket, order) in basketOrder)
            {
                var normalized = Normalize(order);

                // An empty override is "not set": the basket falls back to the global order.
                if (normalized is not null)
                {
                    _basketOrder[basket] = normalized;
                }
            }
        }
    }

    /// <summary>
    /// Gets the effective order for a basket: a non-empty per-basket override, else the non-empty
    /// global list, else <c>null</c> (registration order, all types participate).
    /// </summary>
    /// <param name="basket">The basket name, or <c>null</c> when it is unknown — only the global list applies then.</param>
    public IReadOnlyList<string>? GetOrder(string? basket)
    {
        if (basket is not null && _basketOrder.TryGetValue(basket, out var overrideOrder))
        {
            return overrideOrder;
        }

        return _globalOrder;
    }

    /// <summary>
    /// Orders the candidates by the effective type order of the basket: listed types come first
    /// in the listed order (each type keeping its registration order inside the type),
    /// unlisted types are dropped, listed-but-unregistered types are skipped.
    /// </summary>
    /// <param name="basket">The basket name, or <c>null</c> to apply only the global list.</param>
    /// <param name="candidates">The candidates in registration order.</param>
    public IEnumerable<IFileStorage> Apply(string? basket, IEnumerable<IFileStorage> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var order = GetOrder(basket);
        if (order is null)
        {
            return candidates;
        }

        List<IFileStorage> source = [.. candidates];
        List<IFileStorage> result = new(source.Count);

        foreach (var type in order)
        {
            foreach (var storage in source)
            {
                if (string.Equals(storage.StorageType, type, StringComparison.Ordinal))
                {
                    result.Add(storage);
                }
            }
        }

        return result;
    }

    private static string[]? Normalize(IEnumerable<string>? order)
    {
        if (order is null)
        {
            return null;
        }

        List<string> normalized = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (var type in order)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            var trimmed = type.Trim();
            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized.Count == 0 ? null : [.. normalized];
    }
}
