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
    private record Box(T Value);

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
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private Box CreateAndStore() => _box = new(CreateValue());

    private Box CreatePublicationOnly()
    {
        var newValue = new Box(_valueFactory());
        // Если за это время кто-то уже записал значение, CompareExchange вернет старое, 
        // а наше новое просто уйдет в GC.
        var existing = Interlocked.CompareExchange(ref _box, newValue, null);
        return (existing ?? newValue);
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
        if (mode != LazyThreadSafetyMode.None)
        {
            lock (_syncLock)
            {
                ResetBox();
            }
        }
        else
        {
            ResetBox();
        }
    }

    private void ResetBox()
    {
        Box? oldBox = Interlocked.Exchange(ref _box, null);

        if (oldBox != null && valueReset != null)
        {
            valueReset(oldBox.Value);
        }
    }
}
