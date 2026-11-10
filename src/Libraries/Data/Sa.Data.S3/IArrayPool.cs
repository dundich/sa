namespace Sa.Data.S3;

/// <summary>
/// Пул временных массивов, который клиент использует для буферов загрузки и подписи.
/// </summary>
/// <remarks>
/// Реализация по умолчанию — общий <see cref="System.Buffers.ArrayPool{T}"/>; своя реализация
/// внедряется через конструктор <see cref="S3BucketClient"/> и используется как клиентом, так и
/// <see cref="S3Upload"/>.
/// </remarks>
public interface IArrayPool
{
    /// <summary>
    /// Арендует массив длиной не меньше <paramref name="minimumLength"/>. Возвращённый массив может
    /// быть длиннее.
    /// </summary>
    T[] Rent<T>(int minimumLength);

    /// <summary>
    /// Возвращает арендованный массив в пул.
    /// </summary>
    /// <param name="array">Массив, полученный из этого же пула.</param>
    /// <param name="clear">Очистить массив перед возвратом (для массивов ссылок — обязательно,
    /// иначе пул удерживает их в памяти).</param>
    void ReturnToPool<T>(T[] array, bool clear = false);
}
