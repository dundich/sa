using Sa.Infrastructure;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Sa.Classes;

/// <summary>
/// https://josef.codes/enumeration-class-in-c-sharp-using-records/
/// </summary>
/// <typeparam name="T"></typeparam>
public record Enumeration<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>(int Id, string Name) : IHasId<int>, IComparable<T>
    where T : Enumeration<T>
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

    [DebuggerStepThrough]
    public static IEnumerable<T> GetAll() => AllItems.Value.Values;

    [DebuggerStepThrough]
    public static int DiffId(Enumeration<T> firstId, Enumeration<T> secondId)
        => Math.Abs(firstId.Id - secondId.Id);

    [DebuggerStepThrough]
    public static T FromId(int id)
        => TryFromId(id, out var matchingItem)
            ? matchingItem
            : throw new InvalidOperationException($"'{id}' is not a valid value in {typeof(T)}");

    [DebuggerStepThrough]
    public static T FromName(string name)
        => (TryFromName(name, out var matchingItem))
            ? matchingItem
        : throw new InvalidOperationException($"'{name}' is not a valid display name in {typeof(T)}");

    [DebuggerStepThrough]
    public static bool TryFromName(string name, [MaybeNullWhen(false)] out T item)
        => AllItemsByName.Value.TryGetValue(name, out item);

    [DebuggerStepThrough]
    public static bool TryFromId(int id, [MaybeNullWhen(false)] out T item)
        => AllItems.Value.TryGetValue(id, out item);

    [DebuggerStepThrough]
    public int CompareTo(T? other) => Id.CompareTo(other!.Id);

    public override string ToString() => Name;
}
