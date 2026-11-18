using System.Runtime.CompilerServices;

namespace Sa.Data.S3.Utils;

internal static class CollectionUtils
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Resize<T>(ref T[] array, IArrayPool arrayPool, int newLength, bool clear = false)
    {
        var newArray = arrayPool.Rent<T>(newLength);

        // Rent may return more or less than requested, so copy what actually fits instead of
        // assuming the new buffer is always larger (the old version threw on a shrink).
        var copyLength = Math.Min(array.Length, newArray.Length);
        if (copyLength > 0)
        {
            Array.Copy(array, newArray, copyLength);
            arrayPool.ReturnToPool(array, clear);
        }

        array = newArray;
    }
}
