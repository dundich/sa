using Sa.Media.Echo;

namespace Sa.MediaTests.Echo;

/// <summary>
/// <see cref="WavIO"/> — общий слой ввода-вывода Echo. Проверяется то, что не достижимо
/// через <see cref="CrossFeedSeparator"/>: конвейер отсекает слишком короткие записи раньше,
/// поэтому «не определить размер данных» — это защита самого слоя.
/// </summary>
public sealed class WavIOTests
{
    [Fact]
    public async Task ReadInterleavedFloatsAsync_StreamingHeaderWithoutData_ThrowsInvalidData()
    {
        using TempDirectory directory = new();
        string path = WavTestData.CreateStreamingStereo(directory.Path, 1000, -1000, frames: 0);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => WavIO.ReadInterleavedFloatsAsync(path, TestContext.Current.CancellationToken));
    }
}
