#  AsyncWavReader

Async and memory-efficient WAV file reader for .NET 

This library provides an asynchronous and memory-optimized way to read and process WAV audio files in .NET. It supports:

- PCM 16-bit
- IEEE Float 32-bit
- IEEE Double 64-bit
- Streamable chunks
- Time-based trimming


## Read WAV Header

```csharp
using var stream = File.OpenRead("test.wav");
var reader = new AsyncWavReader(stream);

var header = await reader.GetHeaderAsync();
Console.WriteLine($"Sample Rate: {header.SampleRate}, Channels: {header.NumChannels}");
```

## Read Data

```csharp

    [Fact]
    public async Task ReadRawChannelSamplesAsync_ValidWavFile_YieldsNonEmptyData()
    {
        var pipe = OpenSharedWavFile();
        var reader = new AsyncWavReader(pipe);

        await foreach (var (_, sample, _) in reader.ReadRawChannelSamplesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(sample.Length > 0);
            return;
        }
    }


    [Fact]
    public async Task ReadNormalizedDoubleSamplesAsync_ValidWavFile_YieldsInRangeValues()
    {
        var pipe = OpenSharedWavFile();
        var reader = new AsyncWavReader(pipe);

        await foreach (var (_, sample, _) in reader.ReadNormalizedDoubleSamplesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.InRange(sample, -1.0, 1.0);
            return;
        }
    }

    [Fact]
    public async Task ReadStreamableChunksAsync_ValidWavFile_YieldsChunks()
    {
        var pipe = OpenSharedWavFile();
        var reader = new AsyncWavReader(pipe);

        await foreach (var (_, samples, _) in reader.ReadStreamableChunksAsync(bufferSize: 1024, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(samples.Length > 0);
            return;
        }
    }
```

## Supported Formats

-  ✅ PCM 16 
-  ✅ IEEE Float 32
-  ✅ IEEE Double 64
-  ✅ Stereo / Mono
-  ✅ Extra chunks like "JUNK", "LIST" Automatically skipped
