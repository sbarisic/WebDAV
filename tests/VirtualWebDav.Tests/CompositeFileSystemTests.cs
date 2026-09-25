using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using VirtualWebDav.Sample;

namespace VirtualWebDav.Tests;

public sealed class CompositeFileSystemTests
{
    private static CompositeFileSystem Create(IVirtualFileSystem a, IVirtualFileSystem? b = null)
    {
        return new CompositeFileSystem(new Dictionary<string, IVirtualFileSystem> { ["/FolderA/"] = a, ["/FolderB"] = b ?? new InMemoryFileSystem() });
    }

    private static async Task<List<VirtualEntry>> List(IVirtualFileSystem fs, string path, CancellationToken ct = default)
    {
        var entries = new List<VirtualEntry>();
        await foreach (var entry in fs.EnumerateAsync(path, ct))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static async Task Put(IVirtualFileSystem fs, string path, string text)
    {
        await using var session = await fs.BeginWriteAsync(path, default);
        await session.Stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        await session.CommitAsync(default);
    }

    private static async Task<string> Read(IVirtualFileSystem fs, string path)
    {
        await using var stream = await fs.OpenReadAsync(path, default);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task Error(FileSystemError expected, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<VirtualFileSystemException>(action);
        Assert.Equal(expected, error.Error);
    }

    [Fact]
    public async Task CustomDefaultsAndCancellation()
    {
        var fs = new ReadOnlyFileSystem();
        Func<CancellationToken, Task>[] mutations = [ct => fs.BeginWriteAsync("/hello.txt", ct), ct => fs.CreateDirectoryAsync("/dir", ct), ct => fs.DeleteAsync("/hello.txt", ct), ct => fs.CopyAsync("/hello.txt", "/copy", true, true, ct), ct => fs.MoveAsync("/hello.txt", "/moved", true, ct), ct => fs.PatchPropertiesAsync("/hello.txt", [], [], ct)];
        foreach (var mutation in mutations)
        {
            await Error(FileSystemError.NotSupported, () => mutation(default));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation(new CancellationToken(true)));
        }

        Assert.Empty(await fs.GetPropertiesAsync("/hello.txt", default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fs.GetPropertiesAsync("/hello.txt", new CancellationToken(true)));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/parent/child")]
    [InlineData("relative")]
    [InlineData("/bad//")]
    public void InvalidMountPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => new CompositeFileSystem(new Dictionary<string, IVirtualFileSystem> { [path] = new InMemoryFileSystem() }));
    }

    [Fact]
    public async Task ImmutableRegistrationsAndCasePreservingRootMetadata()
    {
        var a = new InMemoryFileSystem();
        var registration = new Dictionary<string, IVirtualFileSystem>
        {
            ["/FolderA/"] = a
        };
        var fs = new CompositeFileSystem(registration);
        registration.Clear();
        Assert.True((await fs.GetEntryAsync("/", default))!.IsDirectory);
        var entry = await fs.GetEntryAsync("/foldera", default);
        Assert.Equal("FolderA", entry!.Name);
        Assert.Equal((await a.GetEntryAsync("/", default))!.ETag, entry.ETag);
        Assert.Equal("FolderA", Assert.Single(await List(fs, "/")).Name);
        Assert.Null(await fs.GetEntryAsync("/FolderAExtra/file", default));
        Assert.Null(await fs.GetEntryAsync("/missing", default));
        await Error(FileSystemError.NotFound, () => fs.OpenReadAsync("/FolderAExtra/file", default));
        Assert.Empty(await fs.GetPropertiesAsync("/", default));
        Assert.Empty(await List(new CompositeFileSystem(new Dictionary<string, IVirtualFileSystem>()), "/"));
    }

    [Fact]
    public void NullAndDuplicateRegistrationsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new CompositeFileSystem(null!));
        Assert.Throws<ArgumentNullException>(() => new CompositeFileSystem(new Dictionary<string, IVirtualFileSystem> { ["/a"] = null! }));
        Assert.Throws<ArgumentException>(() => new CompositeFileSystem(new Dictionary<string, IVirtualFileSystem> { ["/A"] = new InMemoryFileSystem(), ["/a/"] = new InMemoryFileSystem() }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidProviderRootIsConfigurationError(bool missing)
    {
        var provider = new TrackingFileSystem
        {
            RootOverride = missing ? null : new VirtualEntry("", false, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "\"file\""),
            OverrideRoot = true
        };
        var fs = Create(provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fs.GetEntryAsync("/FolderA/file", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => List(fs, "/"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fs.GetPropertiesAsync("/FolderA", default));
    }

    [Fact]
    public async Task EveryOperationRoutesRelativePathsAndPreservesSessionOwnership()
    {
        var provider = new TrackingFileSystem();
        var fs = Create(provider);
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;
        await fs.CreateDirectoryAsync("/foldera/dir", ct);
        var write = await fs.BeginWriteAsync("/FolderA/dir/file", ct);
        Assert.Same(provider.LastWrite, write);
        await write.Stream.WriteAsync(Encoding.UTF8.GetBytes("content"), ct);
        await write.CommitAsync(ct);
        await write.DisposeAsync();
        Assert.False(write.Stream.CanWrite);
        Assert.Equal("file", (await fs.GetEntryAsync("/FolderA/dir/file", ct))!.Name);
        Assert.Single(await List(fs, "/FolderA/dir", ct));
        var stream = await fs.OpenReadAsync("/FolderA/dir/file", ct);
        Assert.Same(provider.LastRead, stream);
        await stream.DisposeAsync();
        Assert.False(stream.CanRead);
        var property = new XElement(XName.Get("label", "urn:test"), "value");
        await fs.PatchPropertiesAsync("/FolderA", [property], [], ct);
        Assert.Equal("value", Assert.Single(await fs.GetPropertiesAsync("/FolderA", ct)).Value);
        await fs.CopyAsync("/FolderA/dir/file", "/foldera/dir/copied", false, false, ct);
        await fs.MoveAsync("/foldera/dir/copied", "/FolderA/dir/COPIED", true, ct);
        await fs.DeleteAsync("/FolderA/dir/COPIED", ct);
        Assert.All(provider.Calls, call => Assert.Equal(ct, call.Token));
        Assert.Contains(provider.Calls, call => call.Operation == "copy" && call.Path == "/dir/file" && call.Destination == "/dir/copied" && !call.Overwrite && !call.Recursive);
        Assert.Contains(provider.Calls, call => call.Operation == "move" && call.Path == "/dir/copied" && call.Destination == "/dir/COPIED" && call.Overwrite);
        Assert.DoesNotContain(provider.Calls, call => call.Path.StartsWith("/FolderA", StringComparison.OrdinalIgnoreCase));
        await using (var aborted = await fs.BeginWriteAsync("/FolderA/dir/file", ct))
        {
            await aborted.Stream.WriteAsync(Encoding.UTF8.GetBytes("discard"), ct);
        }

        Assert.Equal("content", await Read(fs, "/FolderA/dir/file"));
    }

    [Fact]
    public async Task ProtectedRootsAndCrossMountTransfersHaveNoProviderCallbacks()
    {
        var provider = new TrackingFileSystem();
        var fs = Create(provider, provider);
        foreach (string? path in new[]
        {
            "/",
            "/FolderA",
            "/foldera/"
        }

        )
        {
            await Error(FileSystemError.Forbidden, () => fs.BeginWriteAsync(path, default));
            await Error(FileSystemError.Forbidden, () => fs.CreateDirectoryAsync(path, default));
            await Error(FileSystemError.Forbidden, () => fs.DeleteAsync(path, default));
            await Error(FileSystemError.Forbidden, () => fs.CopyAsync(path, "/FolderB/copy", true, true, default));
            await Error(FileSystemError.Forbidden, () => fs.MoveAsync("/FolderB/file", path, true, default));
        }

        await Error(FileSystemError.Forbidden, () => fs.PatchPropertiesAsync("/", [], [], default));
        await Error(FileSystemError.NotSupported, () => fs.CopyAsync("/FolderA/file", "/FolderB/file", true, true, default));
        await Error(FileSystemError.NotSupported, () => fs.MoveAsync("/FolderA/file", "/FolderB/file", true, default));
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task CancellationAndProviderExceptionsArePreserved()
    {
        var provider = new TrackingFileSystem();
        var fs = Create(provider);
        var ct = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fs.GetEntryAsync("/", ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => List(fs, "/", ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fs.BeginWriteAsync("/FolderA/file", ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fs.CopyAsync("/FolderA/a", "/FolderB/b", true, true, ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fs.GetPropertiesAsync("/", ct));
        Assert.Empty(provider.Calls);
        var failure = new VirtualFileSystemException(FileSystemError.InsufficientStorage);
        provider.Failure = failure;
        Assert.Same(failure, await Assert.ThrowsAsync<VirtualFileSystemException>(() => fs.GetEntryAsync("/FolderA/file", default)));
    }

    [Fact]
    public async Task CompositeHttpReadWriteLocksAndReadOnlyDefaults()
    {
        var a = new InMemoryFileSystem();
        var b = new InMemoryFileSystem();
        await Put(a, "/hello.txt", "A");
        await Put(b, "/hello.txt", "B");
        var fs = new CompositeFileSystem(new Dictionary<string, IVirtualFileSystem> { ["/FolderA"] = a, ["/FolderB"] = b, ["/ReadOnly"] = new ReadOnlyFileSystem() });
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var server = await WebDavServer.StartAsync(fs, new WebDavServerOptions { Port = port });
        using var client = new HttpClient
        {
            BaseAddress = server.ListeningUri
        };
        async Task<HttpResponseMessage> Send(string method, string path, string? body = null, params (string Key, string Value)[] headers)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (body != null)
            {
                request.Content = new StringContent(body);
            }

            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return await client.SendAsync(request);
        }

        void Status(int expected, HttpResponseMessage response) => Assert.Equal(expected, (int)response.StatusCode);
        var listing = await Send("PROPFIND", "/", null, ("Depth", "1"));
        Status(207, listing);
        XNamespace dav = "DAV:";
        string[] hrefs = XDocument.Parse(await listing.Content.ReadAsStringAsync()).Descendants(dav + "href").Select(e => e.Value).ToArray();
        Assert.Equal(new[] { "/", "/FolderA/", "/FolderB/", "/ReadOnly/" }, hrefs);
        Assert.Equal("A", await client.GetStringAsync("/foldera/hello.txt"));
        Assert.Equal("B", await client.GetStringAsync("/FolderB/hello.txt"));
        Assert.Equal("read only", await client.GetStringAsync("/ReadOnly/hello.txt"));
        Status(207, await Send("PROPFIND", "/ReadOnly", null, ("Depth", "1")));
        Status(405, await Send("PUT", "/ReadOnly/hello.txt", "denied"));
        Status(405, await Send("MKCOL", "/ReadOnly/new"));
        Status(405, await Send("DELETE", "/ReadOnly/hello.txt"));
        Status(403, await Send("DELETE", "/FolderA"));
        Status(404, await Send("MKCOL", "/NewMount"));
        Status(405, await Send("COPY", "/FolderA/hello.txt", null, ("Destination", "/FolderB/hello.txt")));
        Status(405, await Send("MOVE", "/FolderA/hello.txt", null, ("Destination", "/FolderB/hello.txt")));
        Assert.Equal("A", await Read(a, "/hello.txt"));
        Assert.Equal("B", await Read(b, "/hello.txt"));
        const string lockBody = "<lockinfo xmlns='DAV:'><lockscope><exclusive/></lockscope><locktype><write/></locktype></lockinfo>";
        var locked = await Send("LOCK", "/FolderA/hello.txt", lockBody);
        Status(200, locked);
        string token = locked.Headers.GetValues("Lock-Token").Single();
        Status(423, await Send("PUT", "/FolderA/hello.txt", "blocked"));
        Status(204, await Send("PUT", "/FolderB/hello.txt", "B updated"));
        Status(204, await Send("PUT", "/FolderA/hello.txt", "A updated", ("If", $"({token})")));
        Status(204, await Send("UNLOCK", "/FolderA/hello.txt", null, ("Lock-Token", token)));
        Status(201, await Send("COPY", "/FolderA/hello.txt", null, ("Destination", "/FolderA/copied.txt")));
        Status(201, await Send("MOVE", "/FolderA/copied.txt", null, ("Destination", "/FolderA/moved.txt")));
        Assert.Equal("A updated", await client.GetStringAsync("/FolderA/moved.txt"));
    }

    private sealed class ReadOnlyFileSystem : CustomFileSystem
    {
        private static readonly DateTimeOffset timestamp = DateTimeOffset.UnixEpoch;
        public override Task<VirtualEntry?> GetEntryAsync(string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            VirtualEntry? result = path.ToLowerInvariant() switch
            {
                "/" => new VirtualEntry("", true, 0, timestamp, timestamp, "\"root\""),
                "/hello.txt" => new VirtualEntry("hello.txt", false, 9, timestamp, timestamp, "\"v1\""),
                _ => null
            };
            return Task.FromResult(result);
        }

        public override async IAsyncEnumerable<VirtualEntry> EnumerateAsync(string path, [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (path != "/")
            {
                throw new VirtualFileSystemException(FileSystemError.NotFound);
            }

            yield return (await GetEntryAsync("/hello.txt", ct))!;
        }

        public override Task<Stream> OpenReadAsync(string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!path.Equals("/hello.txt", StringComparison.OrdinalIgnoreCase))
            {
                throw new VirtualFileSystemException(FileSystemError.NotFound);
            }

            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("read only"), false));
        }
    }

    private sealed record Call(string Operation, string Path, CancellationToken Token, string? Destination = null, bool Overwrite = false, bool Recursive = false);
    private sealed class TrackingFileSystem : CustomFileSystem
    {
        private readonly InMemoryFileSystem inner = new();
        public List<Call> Calls { get; } = [];

        public IVirtualWriteSession? LastWrite;
        public Stream? LastRead;
        public bool OverrideRoot;
        public VirtualEntry? RootOverride;
        public Exception? Failure;
        public override Task<VirtualEntry?> GetEntryAsync(string path, CancellationToken ct)
        {
            Calls.Add(new Call("get", path, ct));
            if (Failure != null)
            {
                throw Failure;
            }

            if (path == "/" && OverrideRoot)
            {
                return Task.FromResult(RootOverride);
            }

            return inner.GetEntryAsync(path, ct);
        }

        public override IAsyncEnumerable<VirtualEntry> EnumerateAsync(string path, CancellationToken ct)
        {
            Calls.Add(new Call("enumerate", path, ct));
            return inner.EnumerateAsync(path, ct);
        }

        public override async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
        {
            Calls.Add(new Call("read", path, ct));
            LastRead = await inner.OpenReadAsync(path, ct);
            return LastRead;
        }

        public override async Task<IVirtualWriteSession> BeginWriteAsync(string path, CancellationToken ct)
        {
            Calls.Add(new Call("write", path, ct));
            LastWrite = await inner.BeginWriteAsync(path, ct);
            return LastWrite;
        }

        public override Task CreateDirectoryAsync(string path, CancellationToken ct)
        {
            Calls.Add(new Call("mkdir", path, ct));
            return inner.CreateDirectoryAsync(path, ct);
        }

        public override Task DeleteAsync(string path, CancellationToken ct)
        {
            Calls.Add(new Call("delete", path, ct));
            return inner.DeleteAsync(path, ct);
        }

        public override Task CopyAsync(string source, string destination, bool overwrite, bool recursive, CancellationToken ct)
        {
            Calls.Add(new Call("copy", source, ct, destination, overwrite, recursive));
            return inner.CopyAsync(source, destination, overwrite, recursive, ct);
        }

        public override Task MoveAsync(string source, string destination, bool overwrite, CancellationToken ct)
        {
            Calls.Add(new Call("move", source, ct, destination, overwrite));
            return inner.MoveAsync(source, destination, overwrite, ct);
        }

        public override Task<IReadOnlyList<XElement>> GetPropertiesAsync(string path, CancellationToken ct)
        {
            Calls.Add(new Call("properties", path, ct));
            return inner.GetPropertiesAsync(path, ct);
        }

        public override Task PatchPropertiesAsync(string path, IReadOnlyList<XElement> set, IReadOnlyList<XName> remove, CancellationToken ct)
        {
            Calls.Add(new Call("patch", path, ct));
            return inner.PatchPropertiesAsync(path, set, remove, ct);
        }
    }
}
