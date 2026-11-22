namespace MintPlayer.Spark.Files.Services;

/// <summary>
/// A read-only view of a request body that throws once more than <c>limit</c> bytes have been read, so an over-size
/// upload is cut off while streaming instead of after it has been buffered or stored (#700 §5, size DoS).
/// </summary>
internal sealed class LengthLimitedStream(Stream inner, long limit) : Stream
{
    private long read;

    /// <summary>Whether the body went past the limit.</summary>
    public bool Exceeded { get; private set; }

    /// <summary>The bytes read so far (at most limit + one buffer).</summary>
    public long BytesRead => read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Count(await inner.ReadAsync(buffer, cancellationToken));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

    private int Count(int n)
    {
        read += n;
        if (read > limit)
        {
            Exceeded = true;
            throw new InvalidDataException($"The upload is larger than {limit} bytes.");
        }
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
