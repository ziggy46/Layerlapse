namespace Layerlapse.Core.Printers;

/// <summary>
/// A read-only, seekable view of a file on the printer, fetched in blocks with ranged reads and cached.
/// Lets <see cref="System.IO.Compression.ZipArchive"/> read an archive's directory and one entry without
/// downloading the whole file. Reads block the calling thread, so use it from a background task.
/// </summary>
public sealed class RemoteFileStream(Func<long, int, byte[]> readRange, long length, int blockSize = 64 * 1024) : Stream
{
    private readonly Dictionary<long, byte[]> _blocks = [];
    private long _position;

    /// <summary>Bytes fetched from the printer so far.</summary>
    public long BytesFetched { get; private set; }

    public int RangeReads { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var copied = 0;
        while (count > 0 && _position < length)
        {
            var blockStart = _position / blockSize * blockSize;
            var block = Block(blockStart);
            var inBlock = (int)(_position - blockStart);
            var n = Math.Min(count, block.Length - inBlock);
            if (n <= 0)
            {
                break;
            }

            Buffer.BlockCopy(block, inBlock, buffer, offset, n);
            _position += n;
            offset += n;
            count -= n;
            copied += n;
        }

        return copied;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private byte[] Block(long start)
    {
        if (!_blocks.TryGetValue(start, out var block))
        {
            block = readRange(start, (int)Math.Min(blockSize, length - start));
            _blocks[start] = block;
            BytesFetched += block.Length;
            RangeReads++;
        }

        return block;
    }
}
