using System.Buffers;

namespace Sa.Classes;

internal interface IArrayPool
{
    T[] Rent<T>(int minimumLength);

    void Return<T>(T[] array, bool clear = false);
}

internal sealed class DefaultArrayPool : IArrayPool
{
    public static readonly IArrayPool Shared = new DefaultArrayPool();

    public T[] Rent<T>(int minimumLength)
    {
        return ArrayPool<T>.Shared.Rent(minimumLength);
    }

    public void Return<T>(T[] array, bool clear = false)
    {
        ArrayPool<T>.Shared.Return(array, clear);
    }
}