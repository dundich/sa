using Sa.Media;

namespace Sa.MediaTests;

public class SimpleVadTests
{

    [Theory]
    // [InlineData("./data/pсmS16Le.wav")]
    [InlineData("./data/12345.wav")]
    public async Task CheckVoide_ShouldHasVoice(string filepath)
    {
        SimpleVad vad = new();

        var r = await vad.AnalyzeVoiceAsync(filepath, TestContext.Current.CancellationToken);

        Assert.NotNull(r);
        Assert.True(r.HasVoice);
    }


    [Theory]
    [InlineData("./data/ffout.wav")]
    // [InlineData("./data/output2.wav")]
    public async Task CheckVoide_ShouldNoHasVoice(string filepath)
    {
        SimpleVad vad = new();

        var r = await vad.AnalyzeVoiceAsync(filepath, TestContext.Current.CancellationToken);

        Assert.NotNull(r);
        Assert.False(r.HasVoice);
    }
}
