using System.Buffers;

namespace Sa.Classes;


public interface IArrayPooler<T>
{
    T[] Rent(int minimumLength);
    void Return(T[] array, bool clearArray = false);
}

public interface IArrayPoolFactory
{
    IArrayPooler<T> Create<T>();
}



internal class ArrayPooler<T> : IArrayPooler<T>
{
    public T[] Rent(int minimumLength) => ArrayPool<T>.Shared.Rent(minimumLength);
    public void Return(T[] array, bool clearArray = false) => ArrayPool<T>.Shared.Return(array, clearArray);
}

internal class ArrayPoolFactory : IArrayPoolFactory
{
    public IArrayPooler<T> Create<T>() => new ArrayPooler<T>();
}
