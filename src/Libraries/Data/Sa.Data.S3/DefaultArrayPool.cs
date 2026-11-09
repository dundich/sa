using System.Buffers;

namespace Sa.Data.S3;

/// <summary>
/// Пул по умолчанию: общий <see cref="ArrayPool{T}"/>.
/// </summary>
internal sealed class DefaultArrayPool
{
    private sealed class ArrayPool : IArrayPool
    {
        public T[] Rent<T>(int minimumLength) => ArrayPool<T>.Shared.Rent(minimumLength);
        public void Return<T>(T[] array, bool clear = false) => ArrayPool<T>.Shared.Return(array, clear);
    }

    // Не Lazy-с-подменой: своя реализация подключается через конструктор S3BucketClient, а не
    // глобальной настройкой. Старый SetDefault менял статическое поле после создания экземпляра —
    // это гонка и неявный глобальный стейт, который ни один потребитель не вызывал.
    private static readonly IArrayPool s_instance = new ArrayPool();

    public static IArrayPool Instance => s_instance;
}
