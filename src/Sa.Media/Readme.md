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
var wavReader = new AsyncWavReader(stream);

var header = await wavReader.GetHeaderAsync();
Console.WriteLine($"Sample Rate: {header.SampleRate}, Channels: {header.NumChannels}");
```

## Read WAV Data

```csharp

// Read Normalized Double Samples ([-1.0, 1.0])
await foreach (var (channelId, samples, isEof) in wavReader.ReadNormalizedDoubleSamplesAsync())
{
    Console.WriteLine($"Channel {channelId}, {samples.Length} samples");
}

// Read PCM 16-bit Samples
await foreach (var (channelId, sample, isEof) in wavReader.ReadPcm16BitSamplesAsync())
{
    Console.WriteLine($"Channel {channelId}, {sample.Length} bytes");
}

// Read Streamable Chunks
await foreach (var chunk in wavReader.ReadStreamableChunksAsync(chunkSizeInSamples: 1024))
{
    Console.WriteLine($"Channel {chunk.ChannelId}, {chunk.Data.Length} bytes");
    // Send to audio driver or network
}
```

## Supported Formats

-  ✅ PCM 16 
-  ✅ IEEE Float 32
-  ✅ IEEE Double 64
-  ✅ Stereo / Mono
-  ✅ Extra chunks like "JUNK", "LIST" Automatically skipped
