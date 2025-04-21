using Sa.Data.S3.Utils;
using System.Buffers;

namespace Sa.Data.S3Tests.Utils;

public class CollectionUtilsShould
{
	private static readonly ArrayPool<int> Pool = ArrayPool<int>.Shared;

	[Fact]
	public void ResizeArray()
	{
		var array = Pool.Rent(5);

		var newLength = array.Length * 2;
		CollectionUtils.Resize(ref array, Pool, newLength);
        Assert.True(array.Length >= newLength);
	}

	[Fact]
	public void ResizeEmptyArray()
	{
		const int newLength = 5;
		var emptyArray = Array.Empty<int>();
		CollectionUtils.Resize(ref emptyArray, Pool, newLength);
		Assert.True(emptyArray.Length >= newLength);
	}
}
