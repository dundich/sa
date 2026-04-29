using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Sa.Classes;


/// <summary>
/// Provides support for lazy initialization with reset
/// </summary>
/// <typeparam name="T">The type of object that is being lazily initialized.</typeparam>
[DebuggerStepThrough]
internal sealed class ResetLazy<T>(
    Func<T> valueFactory,
    LazyThreadSafetyMode mode = LazyThreadSafetyMode.ExecutionAndPublication,
    Action<T>? valueReset = null)
{
    private sealed record Box(T Value);

    private readonly Func<T> _valueFactory = valueFactory
        ?? throw new ArgumentNullException(nameof(valueFactory));

    private readonly Lock _syncLock = new();

    private volatile Box? _box;

    public T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_box ?? Initialize()).Value;
    }

    public bool IsValueCreated => _box != null;

    private Box Initialize()
    {
        return mode switch
        {
            LazyThreadSafetyMode.None => CreateAndStore(),
            LazyThreadSafetyMode.PublicationOnly => CreatePublicationOnly(),
            LazyThreadSafetyMode.ExecutionAndPublication => CreateExecutionAndPublication(),
            _ => throw new InvalidOperationException($"Unsupported thread safety mode: {mode}")
        };
    }

    private Box CreateAndStore() => _box = new(CreateValue());

    private Box CreatePublicationOnly()
    {
        var newBox = new Box(_valueFactory());
        var existing = Interlocked.CompareExchange(ref _box, newBox, null);
        return (existing ?? newBox);
    }

    private Box CreateExecutionAndPublication()
    {
        lock (_syncLock)
        {
            return _box ?? CreateAndStore();
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T CreateValue() => _valueFactory();

    public void Load() => _ = Value;



    public void Reset()
    {
        Box? oldBox = Interlocked.Exchange(ref _box, null);

        if (oldBox != null && valueReset != null)
        {
            valueReset(oldBox.Value);
        }
    }
}
