using System.Diagnostics;

namespace Sa.Classes;

public interface IResetLazy
{
    object? Value { get; }
    void Reset();
    void Load();
}


/// <summary>
/// Provides support for lazy initialization with reset
/// </summary>
/// <typeparam name="T">The type of object that is being lazily initialized.</typeparam>
[DebuggerStepThrough]
public sealed class ResetLazy<T>(Func<T> valueFactory, LazyThreadSafetyMode mode = LazyThreadSafetyMode.ExecutionAndPublication, Action<T>? valueReset = null) : IResetLazy
{
    record Box(T Value);

    private readonly Func<T> _valueFactory = valueFactory ?? throw new ArgumentNullException(nameof(valueFactory));

    private readonly Lock _syncLock = new();

    private Box? _box;

    public T Value
    {
        [DebuggerStepThrough]
        get
        {
            if (_box != null)
            {
                return _box.Value;
            }

            return mode switch
            {
                LazyThreadSafetyMode.ExecutionAndPublication => GetValueWithLock(),
                LazyThreadSafetyMode.PublicationOnly => GetValueWithPublicationOnly(),
                _ => CreateValue(),
            };
        }
    }

    private T GetValueWithLock()
    {
        lock (_syncLock)
        {
            Box? b2 = _box;
            if (b2 != null) return b2.Value;

            _box = new Box(CreateValue());
            return _box.Value;
        }
    }

    private T GetValueWithPublicationOnly()
    {
        T newValue = CreateValue();

        lock (_syncLock)
        {
            Box? b2 = _box;
            if (b2 != null) return b2.Value;

            _box = new Box(newValue);
            return _box.Value;
        }
    }

    private T CreateValue() => _valueFactory();

    public void Load() => _ = Value;

    public bool IsValueCreated => _box != null;

    object? IResetLazy.Value => Value;

    public void Reset()
    {
        if (IsValueCreated)
        {
            lock (_syncLock)
            {
                if (_box != null)
                {
                    valueReset?.Invoke(_box.Value);
                    _box = null;
                }
            }
        }
    }
}
