using Sa.Media.Wav;

namespace Sa.MediaTests;

public class WavFileTests
{
    [Theory]
    [InlineData("Data/m1.wav")]
    public void ReadHeaderTest(string filename)
    {
        using WavFile wavFile = new WavFile()
            .WithFileName(filename)
            .ReadHeader();

        Assert.NotNull(wavFile);
        Assert.True(wavFile.IsPcmWave);
    }


    [Theory]
    [InlineData("Data/m1.wav")]
    public void WriteChannelTest(string filename)
    {
        using WavFile wavFile = new WavFile().WithFileName(filename);

        var file = Path.Combine(Path.GetDirectoryName(filename)!, Path.GetFileNameWithoutExtension(filename) + "_02.wav");

        long len = wavFile.WriteChannel(file, 1);

        Assert.True(len > 0);

        File.Delete(file);
    }
}