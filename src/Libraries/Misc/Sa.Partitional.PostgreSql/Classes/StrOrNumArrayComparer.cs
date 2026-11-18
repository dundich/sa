namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Structural equality for a list-partition branch: <c>["tenant_a", "EU"]</c> equals
/// <c>["tenant_a", "EU"]</c> element by element, using <see cref="StrOrNum"/>'s own value equality.
/// </summary>
/// <remarks>
/// Array identity is not equality: <c>Distinct</c>/<c>GroupBy</c> over <c>StrOrNum[]</c> with the
/// default comparer keeps every row. This type is what makes the deduplication of the branches read
/// from an adopted table work without inventing a string key — a joined key such as
/// <c>string.Join("|", …)</c> is not injective, because a value may itself contain the joiner
/// (a <c>"a|s:b"</c> value collided with the pair <c>"a"</c> + <c>"b|s:c"</c> and silently dropped a
/// branch).
/// </remarks>
internal sealed class StrOrNumArrayComparer : IEqualityComparer<StrOrNum[]>
{
    public static readonly StrOrNumArrayComparer Instance = new();

    public bool Equals(StrOrNum[]? x, StrOrNum[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.Length != y.Length) return false;

        return x.AsSpan().SequenceEqual(y);
    }

    public int GetHashCode(StrOrNum[] obj)
    {
        HashCode hash = new();
        foreach (StrOrNum value in obj) hash.Add(value);

        return hash.ToHashCode();
    }
}
