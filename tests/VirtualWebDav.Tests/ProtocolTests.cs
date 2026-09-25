using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using VirtualWebDav.Sample;

namespace VirtualWebDav.Tests;

public sealed class ProtocolTests : IAsyncLifetime
{
    private static readonly XNamespace D = "DAV:";
    private readonly InMemoryFileSystem fs = new();
    private WebDavServer server = null!;
    private HttpClient client = null!;
    public async Task InitializeAsync()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        server = await WebDavServer.StartAsync(fs, new() { Port = port, MaxXmlBytes = 4096, MaxLockTimeout = TimeSpan.FromSeconds(30) });
        client = new HttpClient
        {
            BaseAddress = server.ListeningUri
        };
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await server.DisposeAsync();
    }

    private async Task<HttpResponseMessage> Send(string method, string path, string? body = null, params (string Key, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        }

        foreach (var h in headers)
        {
            request.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        return await client.SendAsync(request);
    }

    private static void Status(int status, HttpResponseMessage response) => Assert.Equal(status, (int)response.StatusCode);
    private async Task Put(string path, string value)
    {
        using var r = await Send("PUT", path, value);
        Assert.True(r.IsSuccessStatusCode);
    }

    private const string LockBody = "<lockinfo xmlns='DAV:'><lockscope><exclusive/></lockscope><locktype><write/></locktype><owner>test</owner></lockinfo>";
    [Fact]
    public async Task FullDriveLifecycleAndUnicode()
    {
        Status(200, await Send("OPTIONS", "/"));
        Status(201, await Send("MKCOL", "/Folder"));
        await Put("/Folder/%C4%8D%20%26.txt", "first");
        var listing = await Send("PROPFIND", "/Folder", null, ("Depth", "1"));
        Status(207, listing);
        var xml = XDocument.Parse(await listing.Content.ReadAsStringAsync());
        Assert.Contains(xml.Descendants(D + "href"), p => p.Value == "/Folder/%C4%8D%20%26.txt");
        Assert.Equal("first", await client.GetStringAsync("/folder/%C4%8D%20%26.txt"));
        Status(200, await Send("HEAD", "/Folder/%C4%8D%20%26.txt"));
        Status(201, await Send("COPY", "/Folder", null, ("Destination", new Uri(client.BaseAddress!, "/Copy").ToString())));
        Assert.Equal("first", await client.GetStringAsync("/Copy/%C4%8D%20%26.txt"));
        Status(412, await Send("COPY", "/Folder", null, ("Destination", "/Copy"), ("Overwrite", "F")));
        Status(201, await Send("MOVE", "/Copy", null, ("Destination", "/Moved")));
        Status(404, await Send("GET", "/Copy/%C4%8D%20%26.txt"));
        Status(204, await Send("DELETE", "/Moved"));
        Status(404, await Send("GET", "/Moved/%C4%8D%20%26.txt"));
    }

    [Fact]
    public async Task RangesAndHttpPreconditions()
    {
        await Put("/a", "0123456789");
        var get = await Send("GET", "/a");
        string tag = get.Headers.ETag!.ToString();
        var range = await Send("GET", "/a", null, ("Range", "bytes=2-5"));
        Status(206, range);
        Assert.Equal("2345", await range.Content.ReadAsStringAsync());
        Assert.Equal("bytes 2-5/10", range.Content.Headers.ContentRange!.ToString());
        Assert.Equal("89", await (await Send("GET", "/a", null, ("Range", "bytes=-2"))).Content.ReadAsStringAsync());
        Status(416, await Send("GET", "/a", null, ("Range", "bytes=99-")));
        Status(304, await Send("GET", "/a", null, ("If-None-Match", tag)));
        Status(412, await Send("PUT", "/a", "bad", ("If-Match", "\"wrong\"")));
        Status(412, await Send("PUT", "/a", "bad", ("If-None-Match", "*")));
        Status(204, await Send("PUT", "/a", "ok", ("If-Match", tag)));
        Assert.Equal("ok", await client.GetStringAsync("/a"));
    }

    [Fact]
    public async Task LockCreateRefreshRejectAndExpire()
    {
        var locked = await Send("LOCK", "/new", LockBody, ("Depth", "0"));
        Status(201, locked);
        string token = locked.Headers.GetValues("Lock-Token").Single();
        Status(423, await Send("PUT", "/new", "denied"));
        Status(412, await Send("PUT", "/new", "denied", ("If", "(Not " + token + ")")));
        Status(204, await Send("PUT", "/new", "allowed", ("If", "(" + token + ")")));
        Status(200, await Send("LOCK", "/new", null, ("If", "(" + token + ")"), ("Timeout", "Second-1")));
        Status(409, await Send("UNLOCK", "/new", null, ("Lock-Token", "<urn:uuid:wrong>")));
        await Task.Delay(1200);
        Status(204, await Send("PUT", "/new", "expired"));
        var again = await Send("LOCK", "/new", LockBody);
        Status(204, await Send("UNLOCK", "/new", null, ("Lock-Token", again.Headers.GetValues("Lock-Token").Single())));
    }

    [Fact]
    public async Task CollectionLocksAndTaggedDestination()
    {
        Status(201, await Send("MKCOL", "/dir"));
        await Put("/dir/a", "a");
        await Put("/source", "b");
        var locked = await Send("LOCK", "/dir", LockBody, ("Depth", "infinity"));
        string token = locked.Headers.GetValues("Lock-Token").Single();
        Status(423, await Send("DELETE", "/dir/a"));
        Status(423, await Send("COPY", "/source", null, ("Destination", "/dir/a")));
        Status(204, await Send("COPY", "/source", null, ("Destination", "/dir/a"), ("If", $"<{client.BaseAddress}dir> ({token})")));
        Status(204, await Send("DELETE", "/dir", null, ("If", $"({token})")));
    }

    [Fact]
    public async Task DepthZeroCollectionLockProtectsMembershipButNotChildContents()
    {
        Status(201, await Send("MKCOL", "/dir"));
        await Put("/dir/a", "a");
        var locked = await Send("LOCK", "/dir", LockBody, ("Depth", "0"));
        string token = locked.Headers.GetValues("Lock-Token").Single();
        Status(204, await Send("PUT", "/dir/a", "updated"));
        Status(423, await Send("PUT", "/dir/b", "new"));
        Status(423, await Send("DELETE", "/dir/a"));
        Status(201, await Send("PUT", "/dir/b", "new", ("If", $"<{client.BaseAddress}dir> ({token})")));
    }

    [Fact]
    public async Task OverwritingLockedDestinationPreservesItsLock()
    {
        await Put("/a", "a");
        await Put("/b", "b");
        var locked = await Send("LOCK", "/b", LockBody, ("Depth", "0"));
        string token = locked.Headers.GetValues("Lock-Token").Single();
        Status(204, await Send("COPY", "/a", null, ("Destination", "/b"), ("If", $"<{client.BaseAddress}b> ({token})")));
        Status(423, await Send("PUT", "/b", "denied"));
        Status(204, await Send("UNLOCK", "/b", null, ("Lock-Token", token)));
    }

    [Fact]
    public async Task CaseOnlyRenameAndShallowCopy()
    {
        Status(201, await Send("MKCOL", "/Folder"));
        await Put("/Folder/file", "data");
        Status(204, await Send("MOVE", "/Folder", null, ("Destination", "/folder")));
        Assert.Equal("folder", (await fs.GetEntryAsync("/folder", default))!.Name);
        Assert.Equal("data", await client.GetStringAsync("/folder/file"));
        Status(201, await Send("COPY", "/folder", null, ("Destination", "/empty"), ("Depth", "0")));
        Status(404, await Send("GET", "/empty/file"));
    }

    [Fact]
    public async Task PropertiesAreAtomicAndXmlIsBounded()
    {
        await Put("/a", "");
        const string patch = "<propertyupdate xmlns='DAV:'><set><prop><x:label xmlns:x='urn:test'>a &amp; b</x:label></prop></set></propertyupdate>";
        Status(207, await Send("PROPPATCH", "/a", patch));
        var props = await Send("PROPFIND", "/a", "<propfind xmlns='DAV:'><prop><x:label xmlns:x='urn:test'/><missing/></prop></propfind>", ("Depth", "0"));
        var xml = XDocument.Parse(await props.Content.ReadAsStringAsync());
        Assert.Equal("a & b", xml.Descendants(XName.Get("label", "urn:test")).Single().Value);
        Assert.Contains(xml.Descendants(D + "status"), s => s.Value.Contains("404"));
        var rejected = await Send("PROPPATCH", "/a", "<propertyupdate xmlns='DAV:'><set><prop><getetag>bad</getetag><x:label xmlns:x='urn:test'>changed</x:label></prop></set></propertyupdate>");
        Assert.Contains("424", await rejected.Content.ReadAsStringAsync());
        Assert.Equal("a & b", (await fs.GetPropertiesAsync("/a", default)).Single().Value);
        Status(400, await Send("PROPFIND", "/", "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///c:/windows/win.ini'>]><propfind xmlns='DAV:'><allprop/>&e;</propfind>", ("Depth", "0")));
        Status(413, await Send("PROPFIND", "/", new string('x', 5000), ("Depth", "0")));
        Status(403, await Send("PROPFIND", "/", null, ("Depth", "infinity")));
    }

    [Fact]
    public async Task LocalBoundaryAndErrorMapping()
    {
        Status(403, await Send("PUT", "/a", "bad", ("Origin", "https://example.com")));
        Status(403, await Send("GET", "/", null, ("Host", "evil.test:" + client.BaseAddress!.Port)));
        Status(400, await Send("GET", "/bad%2fname"));
        Status(400, await Send("GET", "/bad%5cname"));
        Status(400, await Send("GET", "/bad%FFname"));
        Status(409, await Send("PUT", "/absent/a", "a"));
        Status(404, await Send("DELETE", "/absent"));
        await Put("/a", "a");
        Status(502, await Send("COPY", "/a", null, ("Destination", "http://example.com/a")));
        Status(400, await Send("COPY", "/a", null, ("Destination", client.BaseAddress + "folder/../b")));
        Status(415, await Send("MKCOL", "/dir", "not xml"));
    }

    [Fact]
    public async Task CompetingConditionalWritesHaveOneWinner()
    {
        await Put("/a", "old");
        string tag = (await Send("HEAD", "/a")).Headers.ETag!.ToString();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Send("PUT", "/a", i.ToString(), ("If-Match", tag))));
        Assert.Single(results.Where(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed));
    }

    [Fact]
    public async Task TruncatedNetworkUploadDoesNotCommit()
    {
        await Put("/a", "original");
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync("localhost", client.BaseAddress!.Port);
            byte[] data = Encoding.ASCII.GetBytes($"PUT /a HTTP/1.1\r\nHost: localhost:{client.BaseAddress.Port}\r\nContent-Length: 100000\r\nConnection: close\r\n\r\npartial");
            var network = tcp.GetStream();
            await network.WriteAsync(data);
            tcp.Client.Shutdown(SocketShutdown.Send);
            await network.CopyToAsync(Stream.Null);
        }

        Assert.Equal("original", await client.GetStringAsync("/a"));
    }

    [Theory]
    [InlineData("/../x")]
    [InlineData("/a//b")]
    [InlineData("/a//")]
    [InlineData("/CON.txt")]
    [InlineData("/a.")]
    [InlineData("/a\\b")]
    public void InvalidPaths(string path) => Assert.Throws<ArgumentException>(() => VirtualPath.Normalize(path));
    [Fact]
    public async Task AbortedWriteSessionAndCancellationPreserveData()
    {
        await Put("/a", "original");
        await using (var write = await fs.BeginWriteAsync("/a", default))
        {
            await write.Stream.WriteAsync(Encoding.UTF8.GetBytes("uncommitted"));
        }

        Assert.Equal("original", await client.GetStringAsync("/a"));
        await using var cancelled = await fs.BeginWriteAsync("/a", default);
        await cancelled.Stream.WriteAsync(Encoding.UTF8.GetBytes("cancelled"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.CommitAsync(new CancellationToken(true)));
        Assert.Equal("original", await client.GetStringAsync("/a"));
    }
}
