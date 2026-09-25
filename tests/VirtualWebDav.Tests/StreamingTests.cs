using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;

namespace VirtualWebDav.Tests;

public sealed class StreamingTests
{
    [Fact]
    public async Task LargeTransfersUseBoundedBuffersAndDisposeStreams()
    {
        const long length = 32L * 1024 * 1024;
        var provider = new StreamingProvider(length);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var server = await WebDavServer.StartAsync(provider, new() { Port = port });
        using var client = new HttpClient
        {
            BaseAddress = server.ListeningUri
        };
        using (var input = new ZeroStream(length))
        using (var body = new StreamContent(input))
        {
            body.Headers.ContentLength = length;
            using var response = await client.PutAsync("/file", body);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.True(provider.Committed);
        Assert.True(provider.WriteDisposed);
        Assert.Equal(length, provider.Written);
        Assert.InRange(provider.LargestWrite, 1, 65536);
        // Range is ignored for a non-seekable provider stream, returning a complete 200.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/file");
        request.Headers.Range = new(5, 9);
        using var download = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(length, download.Content.Headers.ContentLength);
        await using var data = await download.Content.ReadAsStreamAsync();
        byte[] buffer = new byte[32768];
        long received = 0;
        int n;
        while ((n = await data.ReadAsync(buffer)) > 0)
        {
            received += n;
            Assert.Equal(-1, buffer.AsSpan(0, n).IndexOfAnyExcept((byte)0));
        }

        Assert.Equal(length, received);
        // Server disposal waits until response processing has finished.
        for (int i = 0; i < 100 && !provider.ReadDisposed; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(provider.ReadDisposed);
        Assert.InRange(provider.LargestRead, 1, 65536);
    }

    private sealed class ZeroStream(long length, Action<int>? onRead = null, Action? onDispose = null) : Stream
    {
        private long remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            onRead?.Invoke(buffer.Length);
            int n = (int)Math.Min(buffer.Length, remaining);
            buffer[..n].Clear();
            remaining -= n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            onDispose?.Invoke();
            base.Dispose(disposing);
        }
    }

    private sealed class SinkStream(Action<int> onWrite) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => onWrite(count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            onWrite(buffer.Length);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StreamingProvider(long length) : IVirtualFileSystem
    {
        public bool Committed, ReadDisposed, WriteDisposed;
        public long Written;
        public int LargestRead, LargestWrite;
        public Task<VirtualEntry?> GetEntryAsync(string path, CancellationToken ct) => Task.FromResult<VirtualEntry?>(new("file", false, length, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "\"v1\""));
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => Task.FromResult<Stream>(new ZeroStream(length, n => LargestRead = Math.Max(LargestRead, n), () => ReadDisposed = true));
        public Task<IVirtualWriteSession> BeginWriteAsync(string path, CancellationToken ct) => Task.FromResult<IVirtualWriteSession>(new Session(this));
        private sealed class Session : IVirtualWriteSession
        {
            private readonly StreamingProvider owner;
            public Session(StreamingProvider owner)
            {
                this.owner = owner;
                Stream = new SinkStream(n =>
                {
                    owner.Written += n;
                    owner.LargestWrite = Math.Max(n, owner.LargestWrite);
                });
            }

            public Stream Stream { get; }

            public Task CommitAsync(CancellationToken ct)
            {
                owner.Committed = true;
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                owner.WriteDisposed = true;
                await Stream.DisposeAsync();
            }
        }

        public IAsyncEnumerable<VirtualEntry> EnumerateAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task CopyAsync(string source, string destination, bool overwrite, bool recursive, CancellationToken ct) => throw new NotSupportedException();
        public Task MoveAsync(string source, string destination, bool overwrite, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<XElement>> GetPropertiesAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task PatchPropertiesAsync(string path, IReadOnlyList<XElement> set, IReadOnlyList<XName> remove, CancellationToken ct) => throw new NotSupportedException();
    }
}
