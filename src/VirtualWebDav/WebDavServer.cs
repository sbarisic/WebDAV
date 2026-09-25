using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VirtualWebDav;

public sealed partial class WebDavServer : IAsyncDisposable
{
    internal static readonly XNamespace Dav = "DAV:";
    private readonly IVirtualFileSystem fs;
    private readonly WebDavServerOptions options;
    private readonly SemaphoreSlim mutations = new(1);
    private readonly List<DavLock> locks = [];
    private WebApplication app = null!;
    public Uri ListeningUri { get; private set; } = null!;
    private WebDavServer(IVirtualFileSystem provider, WebDavServerOptions options) { fs = provider; this.options = options; }

    public static async Task<WebDavServer> StartAsync(IVirtualFileSystem provider, WebDavServerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        options ??= new();
        if (options.Port is < 1 or > 65535 || options.MaxXmlBytes < 1 || options.MaxLockTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        var server = new WebDavServer(provider, options);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = null;
            k.ListenLocalhost(options.Port);
        });
        server.app = builder.Build();
        server.ListeningUri = new Uri($"http://localhost:{options.Port}/");
        server.app.Run(server.HandleAsync);
        try { await server.app.StartAsync(cancellationToken); }
        catch { await server.app.DisposeAsync(); throw; }
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await app.StopAsync(timeout.Token); }
        finally { await app.DisposeAsync(); mutations.Dispose(); }
    }

    private sealed class DavException(int status, string? condition = null) : Exception
    {
        public int Status { get; } = status;
        public string? Condition { get; } = condition;
    }

    private async Task HandleAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        bool acquired = false;
        try
        {
            ValidateRequest(context);
            var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path.Value ?? "/";
            var path = DecodePath(raw.Split('?')[0]);
            // Serialize all requests to keep metadata/content snapshots and lock checks consistent.
            await mutations.WaitAsync(ct); acquired = true;
            locks.RemoveAll(l => l.Expires <= DateTimeOffset.UtcNow);
            context.Response.Headers["MS-Author-Via"] = "DAV";
            switch (context.Request.Method)
            {
                case "OPTIONS":
                    context.Response.Headers["DAV"] = "1, 2";
                    context.Response.Headers["Allow"] = "OPTIONS, PROPFIND, PROPPATCH, GET, HEAD, PUT, MKCOL, DELETE, COPY, MOVE, LOCK, UNLOCK";
                    context.Response.StatusCode = 200; break;
                case "PROPFIND": await PropFindAsync(context, path); break;
                case "GET": case "HEAD": await ReadAsync(context, path); break;
                case "LOCK": await LockAsync(context, path); break;
                case "UNLOCK": await UnlockAsync(context, path); break;
                default: await MutateAsync(context, path); break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { context.Abort(); }
        catch (Exception e)
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.Clear();
            var status = e switch
            {
                DavException de => de.Status,
                VirtualFileSystemException ve => ve.Error switch
                {
                    FileSystemError.NotFound => 404, FileSystemError.Forbidden => 403,
                    FileSystemError.AlreadyExists => 412, FileSystemError.Conflict => 409,
                    FileSystemError.InsufficientStorage => 507, FileSystemError.NotSupported => 405, _ => 500
                },
                ArgumentException or XmlException or FormatException => 400,
                BadHttpRequestException be => be.StatusCode,
                _ => 500
            };
            context.Response.StatusCode = status;
            if (status == 500) app.Logger.LogError(e, "WebDAV provider/request failure");
            if (e is DavException { Condition: not null } error)
                await XmlAsync(context, new XElement(Dav + "error", new XElement(Dav + error.Condition)), status);
        }
        finally { if (acquired) mutations.Release(); }
    }

    private void ValidateRequest(HttpContext c)
    {
        var host = c.Request.Host;
        if (host.Port != options.Port || !(host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(host.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip))) throw new DavException(403);
        if (c.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)) throw new DavException(403);
        if (c.Request.Headers.ContainsKey("Origin") || c.Request.Headers.ContainsKey("Sec-Fetch-Site")) throw new DavException(403);
    }

    private static string DecodePath(string escaped)
    {
        for (int i = 0; i < escaped.Length; i++)
            if (escaped[i] == '%' && (i + 2 >= escaped.Length || !Uri.IsHexDigit(escaped[i + 1]) || !Uri.IsHexDigit(escaped[i + 2])))
                throw new DavException(400);
        if (escaped.Contains("%2f", StringComparison.OrdinalIgnoreCase) || escaped.Contains("%5c", StringComparison.OrdinalIgnoreCase)) throw new DavException(400);
        var decoded = new StringBuilder();
        var utf8 = new UTF8Encoding(false, true);
        for (int i = 0; i < escaped.Length;)
        {
            if (escaped[i] != '%') { decoded.Append(escaped[i++]); continue; }
            var bytes = new List<byte>();
            while (i < escaped.Length && escaped[i] == '%')
            {
                bytes.Add(Convert.ToByte(escaped.Substring(i + 1, 2), 16)); i += 3;
            }
            decoded.Append(utf8.GetString(bytes.ToArray()));
        }
        return VirtualPath.Normalize(decoded.ToString());
    }

    private async Task<XElement?> ReadXmlAsync(HttpContext c)
    {
        if (c.Request.ContentLength > options.MaxXmlBytes) throw new DavException(413);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await c.Request.Body.ReadAsync(bytes, c.RequestAborted)) > 0)
        {
            if (buffer.Length + read > options.MaxXmlBytes) throw new DavException(413);
            await buffer.WriteAsync(bytes.AsMemory(0, read), c.RequestAborted);
        }
        if (buffer.Length == 0) return null;
        buffer.Position = 0;
        using var reader = XmlReader.Create(buffer, new XmlReaderSettings
        {
            Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = options.MaxXmlBytes
        });
        return (await XDocument.LoadAsync(reader, LoadOptions.None, c.RequestAborted)).Root;
    }

    private static async Task XmlAsync(HttpContext c, XElement element, int status = 207)
    {
        c.Response.StatusCode = status;
        c.Response.ContentType = "application/xml; charset=utf-8";
        await c.Response.WriteAsync("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + element.ToString(SaveOptions.DisableFormatting), c.RequestAborted);
    }

    private async Task<VirtualEntry> RequiredAsync(string path, CancellationToken ct) =>
        await fs.GetEntryAsync(path, ct) ?? throw new DavException(404);

    private async Task ReadAsync(HttpContext c, string path)
    {
        var entry = await RequiredAsync(path, c.RequestAborted);
        if (entry.IsDirectory) throw new DavException(405);
        c.Response.Headers.ETag = entry.ETag;
        c.Response.Headers.LastModified = entry.Modified.ToString("R");
        if (!await PreconditionsAsync(c, path, entry, true)) return;
        c.Response.ContentType = "application/octet-stream";
        c.Response.ContentLength = entry.Length;
        if (c.Request.Method == "HEAD") return;
        await using var stream = await fs.OpenReadAsync(path, c.RequestAborted);
        long count = entry.Length;
        if (stream.CanSeek)
        {
            c.Response.Headers.AcceptRanges = "bytes";
            var range = c.Request.Headers.Range.ToString();
            var ifRange = c.Request.Headers.IfRange.ToString();
            bool allowRange = ifRange.Length == 0 || ifRange == entry.ETag ||
                DateTimeOffset.TryParse(ifRange, out var date) && entry.Modified.ToUnixTimeSeconds() <= date.ToUnixTimeSeconds();
            if (allowRange && range.StartsWith("bytes=") && !range.Contains(','))
            {
                var parts = range[6..].Split('-');
                if (parts.Length != 2) throw new DavException(400);
                long start, end;
                if (parts[0].Length == 0)
                {
                    if (!long.TryParse(parts[1], out var suffix) || suffix <= 0) throw new DavException(416);
                    start = Math.Max(0, count - suffix); end = count - 1;
                }
                else
                {
                    if (!long.TryParse(parts[0], out start)) throw new DavException(400);
                    end = parts[1].Length == 0 ? count - 1 : long.TryParse(parts[1], out var parsed) ? Math.Min(parsed, count - 1) : throw new DavException(400);
                }
                if (start < 0 || start >= count || end < start)
                {
                    c.Response.StatusCode = 416; c.Response.ContentLength = 0;
                    c.Response.Headers.ContentRange = $"bytes */{count}"; return;
                }
                stream.Seek(start, SeekOrigin.Begin);
                c.Response.StatusCode = 206;
                c.Response.Headers.ContentRange = $"bytes {start}-{end}/{count}";
                count = end - start + 1; c.Response.ContentLength = count;
            }
        }
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), c.RequestAborted);
            if (n == 0) throw new IOException("Provider stream shorter than advertised length.");
            await c.Response.Body.WriteAsync(buffer.AsMemory(0, n), c.RequestAborted);
            count -= n;
        }
    }

    private async Task MutateAsync(HttpContext c, string path)
    {
        if (c.Request.Method is not ("PUT" or "MKCOL" or "DELETE" or "COPY" or "MOVE" or "PROPPATCH")) throw new DavException(405);
        var ct = c.RequestAborted;
        var entry = await fs.GetEntryAsync(path, ct);
        await PreconditionsAsync(c, path, entry);
        if (path == "/" && c.Request.Method != "PROPPATCH") throw new DavException(403);
        if (c.Request.Method != "COPY") CheckLocks(c, path, c.Request.Method is "DELETE" or "MOVE");
        if (c.Request.Method is "MKCOL" or "DELETE" or "MOVE" || c.Request.Method == "PUT" && entry == null)
            CheckLocks(c, VirtualPath.Parent(path), false);
        switch (c.Request.Method)
        {
            case "PUT":
                if (entry?.IsDirectory == true) throw new DavException(405);
                if (c.Request.Headers.ContainsKey("Content-Range")) throw new DavException(400);
                await using (var write = await fs.BeginWriteAsync(path, ct))
                {
                    await c.Request.Body.CopyToAsync(write.Stream, 64 * 1024, ct);
                    await write.Stream.FlushAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    await write.CommitAsync(ct);
                }
                c.Response.StatusCode = entry == null ? 201 : 204;
                break;
            case "MKCOL":
                if (entry != null) throw new DavException(405);
                if (await c.Request.Body.ReadAsync(new byte[1], ct) != 0) throw new DavException(415);
                await fs.CreateDirectoryAsync(path, ct); c.Response.StatusCode = 201; break;
            case "DELETE":
                await RequiredAsync(path, ct); await fs.DeleteAsync(path, ct);
                locks.RemoveAll(l => VirtualPath.Within(l.Path, path));
                c.Response.StatusCode = 204; break;
            case "COPY": case "MOVE":
                await RequiredAsync(path, ct);
                var destination = Destination(c);
                bool caseRename = c.Request.Method == "MOVE" && destination.Equals(path, StringComparison.OrdinalIgnoreCase) && destination != path;
                if (destination == "/" || !caseRename && (VirtualPath.Within(destination, path) || VirtualPath.Within(path, destination))) throw new DavException(403);
                var existing = await fs.GetEntryAsync(destination, ct);
                var overwrite = c.Request.Headers["Overwrite"].ToString();
                if (overwrite is not ("" or "T" or "F")) throw new DavException(400);
                if (existing != null && overwrite == "F") throw new DavException(412);
                CheckLocks(c, destination, true);
                CheckLocks(c, VirtualPath.Parent(destination), false);
                var depth = c.Request.Headers["Depth"].ToString();
                if (depth is not ("" or "0" or "infinity") || c.Request.Method == "MOVE" && depth == "0") throw new DavException(400);
                if (c.Request.Method == "COPY") await fs.CopyAsync(path, destination, overwrite != "F", depth != "0", ct);
                else await fs.MoveAsync(path, destination, overwrite != "F", ct);
                if (c.Request.Method == "MOVE" && !caseRename) locks.RemoveAll(l => VirtualPath.Within(l.Path, path));
                c.Response.StatusCode = existing == null ? 201 : 204; break;
            case "PROPPATCH": await PropPatchAsync(c, path); break;
        }
    }

    private string Destination(HttpContext c)
    {
        var value = c.Request.Headers["Destination"].ToString();
        if (string.IsNullOrWhiteSpace(value)) throw new DavException(400);
        if (value.StartsWith('/') && !value.StartsWith("//")) return DecodePath(value);
        return AbsoluteResourcePath(c, value, 502);
    }

    private string AbsoluteResourcePath(HttpContext c, string value, int errorStatus)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Port != options.Port ||
            !uri.Host.Equals(c.Request.Host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            throw new DavException(errorStatus);
        // Keep the original escaped path: System.Uri normalizes dot segments away.
        var slash = value.IndexOf('/', value.IndexOf("://", StringComparison.Ordinal) + 3);
        return DecodePath(slash < 0 ? "/" : value[slash..]);
    }
}
