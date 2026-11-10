namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Fluent description of the list-partition branches a table's migration should create.
/// </summary>
/// <remarks>
/// <para>
/// The shape of the builder mirrors the shape of the partition tree: a <see cref="Node"/> opens one
/// more list level, a <see cref="Branch"/> completes a branch with the remaining values. Nothing can
/// be "the wrong length" by accident — the branch is always the path from the root to the
/// <see cref="Branch"/> call.
/// </para>
/// <para>
/// For a table declared as <c>.PartByList("country", "city")</c>:
/// </para>
/// <code>
/// .AddMigrationTree(t => t
///     .Node("RU", ru => ru
///         .Branch("Moscow")
///         .Branch("Kazan"))        // ["RU","Moscow"], ["RU","Kazan"]
///     .Node("US", us => us
///         .Branch("New York")));   // ["US","New York"]
/// </code>
/// <para>
/// Deeper hierarchies nest further, and <see cref="Branch"/> accepts several values when the rest of
/// the branch is known in one piece: <c>.Node("EU", eu =&gt; eu.Branch("DE", "Berlin"))</c> is
/// <c>["EU","DE","Berlin"]</c>. A one-level list is a flat <c>.Branch("acme")</c>.
/// </para>
/// </remarks>
public interface IMigrationTreeBuilder
{
    /// <summary>
    /// Opens one more list level with <paramref name="value"/> and describes its children.
    /// </summary>
    /// <param name="value">The value of this level.</param>
    /// <param name="children">The values of the next level (or their own nested nodes).</param>
    /// <returns>The same builder, for chaining.</returns>
    IMigrationTreeBuilder Node(StrOrNum value, Action<IMigrationTreeBuilder> children);

    /// <summary>
    /// Completes a branch: the values passed here are appended to the path from the root.
    /// </summary>
    /// <param name="values">The remaining values of the branch (at least one).</param>
    /// <returns>The same builder, for chaining.</returns>
    IMigrationTreeBuilder Branch(params StrOrNum[] values);
}

/// <summary>
/// Collects the branches described through <see cref="IMigrationTreeBuilder"/>.
/// </summary>
/// <remarks>
/// One mutable path stack is enough: the lambdas run depth-first and synchronously, and a nested
/// <see cref="Node"/> pushes/pops around its children.
/// </remarks>
internal sealed class MigrationTreeBuilder : IMigrationTreeBuilder
{
    private readonly List<StrOrNum> _path = [];
    private readonly List<StrOrNum[]> _branches = [];

    /// <summary>The branches collected so far, one value per list level.</summary>
    public IReadOnlyList<StrOrNum[]> Branches => _branches;

    public IMigrationTreeBuilder Node(StrOrNum value, Action<IMigrationTreeBuilder> children)
    {
        ArgumentNullException.ThrowIfNull(children);

        _path.Add(value);

        try
        {
            children(this);
        }
        finally
        {
            _path.RemoveAt(_path.Count - 1);
        }

        return this;
    }

    public IMigrationTreeBuilder Branch(params StrOrNum[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Length == 0)
        {
            throw new ArgumentException(
                "A migration branch needs at least one value; call Branch with the value(s) of the level.",
                nameof(values));
        }

        StrOrNum[] branch = new StrOrNum[_path.Count + values.Length];
        _path.CopyTo(branch);
        values.CopyTo(branch, _path.Count);

        _branches.Add(branch);
        return this;
    }
}
