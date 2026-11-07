using System.Text;
using SupplierFeed.Api.Infrastructure;

namespace SupplierFeed.Tests.Unit;

public class BoundedBodyReaderTests
{
    [Fact]
    public async Task BodyAtLimit_IsRead()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 10)));

        var text = await BoundedBodyReader.ReadAsync(stream, maxBytes: 10, CancellationToken.None);

        Assert.Equal(new string('a', 10), text);
    }

    [Fact]
    public async Task BodyOneByteOverLimit_ReturnsNull()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 11)));

        var text = await BoundedBodyReader.ReadAsync(stream, maxBytes: 10, CancellationToken.None);

        Assert.Null(text);
    }

    [Fact]
    public async Task BodyDeliveredOneByteAtATime_IsReadInFull()
    {
        // Network streams return partial reads; a reader that assumes one read fills the buffer loses data.
        using var stream = new TrickleStream(Encoding.UTF8.GetBytes("hello world"));

        var text = await BoundedBodyReader.ReadAsync(stream, maxBytes: 64, CancellationToken.None);

        Assert.Equal("hello world", text);
    }

    [Fact]
    public async Task OversizedBodyDeliveredOneByteAtATime_ReturnsNull()
    {
        using var stream = new TrickleStream(Encoding.UTF8.GetBytes(new string('a', 30)));

        var text = await BoundedBodyReader.ReadAsync(stream, maxBytes: 10, CancellationToken.None);

        Assert.Null(text);
    }

    /// <summary>Returns at most one byte per read, like a slow network connection.</summary>
    private sealed class TrickleStream : Stream
    {
        private readonly byte[] _bytes;
        private int _position;

        public TrickleStream(byte[] bytes)
        {
            _bytes = bytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _bytes.Length || buffer.Length == 0)
            {
                return ValueTask.FromResult(0);
            }

            buffer.Span[0] = _bytes[_position++];
            return ValueTask.FromResult(1);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
