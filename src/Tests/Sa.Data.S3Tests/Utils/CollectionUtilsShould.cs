using Sa.Data.S3;
using Sa.Data.S3.Utils;

namespace Sa.Data.S3Tests.Utils;

public class CollectionUtilsShould
{
	[Fact]
	public void ResizeArray()
	{
		var array = DefaultArrayPool.Instance.Rent<int>(5);

		var newLength = array.Length * 2;
		CollectionUtils.Resize(ref array, DefaultArrayPool.Instance, newLength);
        Assert.True(array.Length >= newLength);
	}

	[Fact]
	public void ResizeEmptyArray()
	{
		const int newLength = 5;
		var emptyArray = Array.Empty<int>();
		CollectionUtils.Resize(ref emptyArray, DefaultArrayPool.Instance, newLength);
		Assert.True(emptyArray.Length >= newLength);
	}
}
