using Sa;
using System.Text;

namespace SaTests.Classes;

public class PooledMemoryStreamTests
{
    [Fact]
    public void Write_Read_RoundTrips()
    {
        byte[] payload = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();

        using var stream = new PooledMemoryStream();
        stream.Write(payload, 0, payload.Length);

        Assert.Equal(payload.Length, stream.Length);
        Assert.Equal(payload.Length, stream.Position);

        stream.Position = 0;
        byte[] readBack = new byte[payload.Length];
        int bytesRead = stream.Read(readBack, 0, readBack.Length);

        Assert.Equal(payload.Length, bytesRead);
        Assert.Equal(payload, readBack);
        Assert.Equal(-1, stream.ReadByte());
        Assert.Equal(0, stream.Read(readBack, 0, readBack.Length));
    }

    [Fact]
    public void Write_GrowsBeyondInitialCapacity_PreservesContent()
    {
        // Larger than the default 4096-byte buffer, so the stream has to rent a bigger one.
        byte[] payload = Encoding.UTF8.GetBytes(new string('x', 100_000));

        using var stream = new PooledMemoryStream();
        stream.Write(payload, 0, payload.Length / 2);
        stream.Write(payload.AsSpan(payload.Length / 2));

        Assert.Equal(payload.Length, stream.Length);
        Assert.Equal(payload, stream.ToArray());
    }

    [Fact]
    public void SetLength_Zero_RewindsForTheNextPayload()
    {
        // The bulk-insert pattern: one stream serves a whole batch, SetLength(0) rewinds in place
        // and the buffer carries over to the next payload.
        using var stream = new PooledMemoryStream();

        stream.Write(Encoding.UTF8.GetBytes("first payload"));
        stream.Position = 0;
        byte[] first = stream.ToArray();

        stream.SetLength(0);
        stream.Write(Encoding.UTF8.GetBytes("second"));

        Assert.Equal("second", Encoding.UTF8.GetString(stream.ToArray()));
        Assert.NotEqual(first, stream.ToArray());
    }

    [Fact]
    public void SeekPastEnd_ZeroFillsTheGapOnWrite()
    {
        using var stream = new PooledMemoryStream();
        stream.Write(new byte[] { 1, 2, 3 });
        stream.Seek(5, SeekOrigin.Begin);

        stream.WriteByte(9);

        Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 9 }, stream.ToArray());
    }

    [Fact]
    public void Seek_BeforeBegin_Throws()
    {
        using var stream = new PooledMemoryStream();
        stream.Write(new byte[] { 1 });

        Assert.Throws<IOException>(() => stream.Seek(-2, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => stream.Seek(-2, SeekOrigin.End));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)42));
    }

    [Fact]
    public void SetLength_Trims_AndPullsPositionBack()
    {
        using var stream = new PooledMemoryStream();
        stream.Write(new byte[] { 1, 2, 3, 4, 5 });

        stream.SetLength(2);

        Assert.Equal(2, stream.Length);
        Assert.Equal(2, stream.Position);
        Assert.Equal(new byte[] { 1, 2 }, stream.ToArray());
    }

    [Fact]
    public async Task AsyncReadWrite_MatchesSync()
    {
        byte[] payload = Encoding.UTF8.GetBytes("async payload");

        using var stream = new PooledMemoryStream();
        await stream.WriteAsync(payload, TestContext.Current.CancellationToken);

        stream.Position = 0;
        byte[] readBack = new byte[payload.Length];
        int bytesRead = await stream.ReadAsync(readBack.AsMemory(), TestContext.Current.CancellationToken);

        Assert.Equal(payload.Length, bytesRead);
        Assert.Equal(payload, readBack);
    }

    [Fact]
    public void WrittenMemory_MatchesToArray()
    {
        using var stream = new PooledMemoryStream();
        stream.Write(new byte[] { 1, 2, 3 });

        Assert.Equal(stream.ToArray(), stream.WrittenMemory.ToArray());
    }

    [Fact]
    public void AfterDispose_Throws()
    {
        var stream = new PooledMemoryStream();
        stream.Write(new byte[] { 1 });
        stream.Dispose();

        Assert.False(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.False(stream.CanSeek);
        Assert.Throws<ObjectDisposedException>(() => stream.Length);
        Assert.Throws<ObjectDisposedException>(() => stream.ToArray());
        Assert.Throws<ObjectDisposedException>(() => stream.WriteByte(1));
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[1], 0, 1));
    }

    [Fact]
    public void NegativeCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PooledMemoryStream(-1));
    }

    [Fact]
    public void WriteBufferArgumentValidation_Throws()
    {
        using var stream = new PooledMemoryStream();

        Assert.Throws<ArgumentNullException>(() => stream.Write(null!, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Write(new byte[4], -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Write(new byte[4], 0, -1));
        Assert.Throws<ArgumentException>(() => stream.Write(new byte[4], 2, 3));
    }
}
