namespace Sa.Media;

/// <summary>
/// A batch of PCM samples for a single channel.
/// </summary>
/// <param name="Samples">
/// Encoded samples of one channel. The memory is backed by a pooled buffer and is
/// valid only until the next <c>MoveNextAsync</c> of the producing enumeration;
/// copy the data if it must outlive the current iteration.
/// </param>
/// <param name="Position">Byte offset of the first sample from the start of the WAV <c>data</c> chunk.</param>
/// <param name="IsEof"><c>true</c> for the last chunk of the stream.</param>
public readonly record struct AudioChunk(ReadOnlyMemory<byte> Samples, long Position, bool IsEof);
