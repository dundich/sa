using System.Diagnostics;

namespace Sa.Classes;

internal interface IResetLazy
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
internal sealed class ResetLazy<T>(Func<T> valueFactory, LazyThreadSafetyMode mode = LazyThreadSafetyMode.ExecutionAndPublication, Action<T>? valueReset = null) : IResetLazy
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
            Box? b1 = _box;
            if (b1 != null)
                return b1.Value;

            if (mode == LazyThreadSafetyMode.ExecutionAndPublication)
            {
                return LockExecutionAndPublication();
            }
            else if (mode == LazyThreadSafetyMode.PublicationOnly)
            {
                return LockPublicationOnly();
            }
            else
            {
                return CreateAndStoreValuw();
            }
        }
    }

    private T CreateAndStoreValuw()
    {
        Box? b = new(CreateValue());
        _box = b;
        return b.Value;
    }

    private T LockPublicationOnly()
    {
        T newValue = CreateValue();

        lock (_syncLock)
        {
            Box? b2 = _box;
            if (b2 != null)
                return b2.Value;

            _box = new Box(newValue);

            return _box.Value;
        }
    }

    private T LockExecutionAndPublication()
    {
        lock (_syncLock)
        {
            Box? b2 = _box;
            if (b2 != null)
                return b2.Value;

            _box = new Box(CreateValue());

            return _box.Value;
        }
    }

    private T CreateValue() => _valueFactory();

    public void Load() => _ = Value;

    public bool IsValueCreated => _box != null;

    object? IResetLazy.Value => Value;

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
        if (IsValueCreated)
        {
            valueReset?.Invoke(_box!.Value);
            _box = null;
        }
    }
}
