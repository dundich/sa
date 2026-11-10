using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// A base record that implements a type-safe enumeration pattern using reflection-discovered static fields.
/// Inspired by <see href="https://josef.codes/enumeration-class-in-c-sharp-using-records/">Josef Bihl's article</see>.
/// </summary>
/// <typeparam name="T">
/// The derived enumeration type. Enforced via the recursive generic constraint <c>where T : Enumeration{T}</c>.
/// All enum variants must be declared as <c>public static readonly T</c> fields.
/// </typeparam>
/// <param name="Id">Unique integer identifier used for database storage and comparison.</param>
/// <param name="Name">Human-readable display name.</param>
public record Enumeration<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>(int Id, string Name)
    : IComparable<T> where T : Enumeration<T>
{

    private static readonly Lazy<Dictionary<int, T>> AllItems = new(() =>
    {
        return typeof(T)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(x => x.FieldType == typeof(T))
            .Select(x => x.GetValue(null))
            .Cast<T>()
            .ToDictionary(x => x.Id, x => x);
    });

    private static readonly Lazy<Dictionary<string, T>> AllItemsByName = new(() =>
    {
        Dictionary<string, T> items = new(AllItems.Value.Count);
        foreach (T? value in AllItems.Value.Select(c => c.Value))
        {
            items.TryAdd(value.Name, value);
        }
        return items;
    });

    /// <summary>
    /// Returns all registered enumeration values discovered via reflection.
    /// </summary>
    [DebuggerStepThrough]
    internal static IEnumerable<T> GetAll() => AllItems.Value.Values;

    /// <summary>
    /// Computes the absolute difference between the integer IDs of two enumeration values.
    /// Useful for determining adjacency (difference of 1) or distance between variants.
    /// </summary>
    /// <param name="firstId">The first enumeration value.</param>
    /// <param name="secondId">The second enumeration value.</param>
    /// <returns>The absolute difference between their <see cref="Id"/> values.</returns>
    [DebuggerStepThrough]
    internal static int DiffId(Enumeration<T> firstId, Enumeration<T> secondId)
        => Math.Abs(firstId.Id - secondId.Id);

    /// <summary>
    /// Looks up an enumeration value by its integer <see cref="Id"/>.
    /// Throws <see cref="InvalidOperationException"/> when the id is not found.
    /// </summary>
    /// <param name="id">The integer identifier to look up.</param>
    /// <returns>The matching enumeration value.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no variant has the specified id.</exception>
    [DebuggerStepThrough]
    internal static T FromId(int id)
        => TryFromId(id, out var matchingItem)
            ? matchingItem
            : throw new InvalidOperationException($"'{id}' is not a valid value in {typeof(T)}");

    /// <summary>
    /// Looks up an enumeration value by its display <see cref="Name"/>.
    /// Throws <see cref="InvalidOperationException"/> when the name is not found.
    /// </summary>
    /// <param name="name">The display name to look up.</param>
    /// <returns>The matching enumeration value.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no variant has the specified name.</exception>
    [DebuggerStepThrough]
    internal static T FromName(string name)
        => (TryFromName(name, out var matchingItem))
            ? matchingItem
        : throw new InvalidOperationException($"'{name}' is not a valid display name in {typeof(T)}");

    /// <summary>
    /// Attempts to look up an enumeration value by its display <see cref="Name"/>.
    /// </summary>
    /// <param name="name">The display name to look up.</param>
    /// <param name="item">The matching enumeration value, or <c>null</c> if not found.</param>
    /// <returns><c>true</c> if a matching variant was found; otherwise <c>false</c>.</returns>
    /// <remarks>A <c>null</c> name is reported as "not found" rather than throwing from the
    /// underlying dictionary, so a nullable value read from the database can be passed straight in.</remarks>
    [DebuggerStepThrough]
    internal static bool TryFromName(string? name, [MaybeNullWhen(false)] out T item)
    {
        if (name is null)
        {
            item = null;
            return false;
        }

        return AllItemsByName.Value.TryGetValue(name, out item);
    }

    /// <summary>
    /// Attempts to look up an enumeration value by its integer <see cref="Id"/>.
    /// </summary>
    /// <param name="id">The integer identifier to look up.</param>
    /// <param name="item">The matching enumeration value, or <c>null</c> if not found.</param>
    /// <returns><c>true</c> if a matching variant was found; otherwise <c>false</c>.</returns>
    [DebuggerStepThrough]
    internal static bool TryFromId(int id, [MaybeNullWhen(false)] out T item)
        => AllItems.Value.TryGetValue(id, out item);

    /// <summary>
    /// Compares this instance to another enumeration value by their integer <see cref="Id"/>.
    /// </summary>
    /// <param name="other">The other enumeration value to compare with.</param>
    /// <returns>A signed integer indicating the relative order.</returns>
    [DebuggerStepThrough]
    public int CompareTo(T? other) => Id.CompareTo(other!.Id);

    /// <summary>Orders two enumeration values by their integer <see cref="Id"/>.</summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(Enumeration<T> left, Enumeration<T> right) => left.Id < right.Id;

    /// <summary>Orders two enumeration values by their integer <see cref="Id"/>.</summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equal to <paramref name="right"/>.</returns>
    public static bool operator <=(Enumeration<T> left, Enumeration<T> right) => left.Id <= right.Id;

    /// <summary>Orders two enumeration values by their integer <see cref="Id"/>.</summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(Enumeration<T> left, Enumeration<T> right) => left.Id > right.Id;

    /// <summary>Orders two enumeration values by their integer <see cref="Id"/>.</summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equal to <paramref name="right"/>.</returns>
    public static bool operator >=(Enumeration<T> left, Enumeration<T> right) => left.Id >= right.Id;

    /// <summary>
    /// Returns the display <see cref="Name"/> of this enumeration value.
    /// </summary>
    public override string ToString() => Name;
}

/// <summary>
/// Non-generic entry points for reflection-discovered enumeration variants. Lives outside
/// <see cref="Enumeration{T}"/> because a static member on a generic type is per-instantiation
/// and cannot be called without naming the variant type twice.
/// </summary>
public static class Enumeration
{
    /// <summary>Returns all variants of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The enumeration type.</typeparam>
    public static IEnumerable<T> GetAll<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>()
        where T : Enumeration<T>
        => Enumeration<T>.GetAll();

    /// <summary>Absolute difference between the ids of two variants.</summary>
    /// <typeparam name="T">The enumeration type.</typeparam>
    /// <param name="first">The first variant.</param>
    /// <param name="second">The second variant.</param>
    public static int DiffId<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>(T first, T second)
        where T : Enumeration<T>
        => Enumeration<T>.DiffId(first, second);

    /// <summary>Looks up a variant by id; throws when it does not exist.</summary>
    /// <typeparam name="T">The enumeration type.</typeparam>
    /// <param name="id">The id to look up.</param>
    public static T FromId<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>(int id)
        where T : Enumeration<T>
        => Enumeration<T>.FromId(id);

    /// <summary>Looks up a variant by display name; throws when it does not exist.</summary>
    /// <typeparam name="T">The enumeration type.</typeparam>
    /// <param name="name">The display name to look up.</param>
    public static T FromName<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>(string name)
        where T : Enumeration<T>
        => Enumeration<T>.FromName(name);

    /// <summary>Attempts to look up a variant by display name.</summary>
    /// <typeparam name="T">The enumeration type.</typeparam>
    /// <param name="name">The display name to look up.</param>
    /// <param name="item">The matching variant, or <see langword="null"/>.</param>
    public static bool TryFromName<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>(string? name, [MaybeNullWhen(false)] out T item)
        where T : Enumeration<T>
        => Enumeration<T>.TryFromName(name, out item);

    /// <summary>Attempts to look up a variant by id.</summary>
    /// <typeparam name="T">The enumeration type.</typeparam>
    /// <param name="id">The id to look up.</param>
    /// <param name="item">The matching variant, or <see langword="null"/>.</param>
    public static bool TryFromId<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>(int id, [MaybeNullWhen(false)] out T item)
        where T : Enumeration<T>
        => Enumeration<T>.TryFromId(id, out item);
}
