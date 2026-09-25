# Writing your own filesystem

Derive from `CustomFileSystem`. The three required overrides describe the tree and supply file content. Every path is relative to **your provider**, regardless of its mount: `/FolderA/hello.txt` reaches the provider as `/hello.txt`.

This complete read-only provider exposes a single file. It can be passed directly to `WebDavServer.StartAsync` or mounted in a `CompositeFileSystem`.

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using VirtualWebDav;

public sealed class GreetingFileSystem : CustomFileSystem
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("Hello!\r\n");
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.UnixEpoch;

    public override Task<VirtualEntry?> GetEntryAsync(
        string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (path == "/")
        {
            return Task.FromResult<VirtualEntry?>(new VirtualEntry(
                "", true, 0, Timestamp, Timestamp, "\"root-v1\""));
        }

        if (path.Equals("/hello.txt", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<VirtualEntry?>(new VirtualEntry(
                "hello.txt", false, Content.Length,
                Timestamp, Timestamp, "\"hello-v1\""));
        }

        return Task.FromResult<VirtualEntry?>(null);
    }

    public override async IAsyncEnumerable<VirtualEntry> EnumerateAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (path != "/")
        {
            throw new VirtualFileSystemException(FileSystemError.NotFound);
        }

        yield return (await GetEntryAsync("/hello.txt", cancellationToken))!;
    }

    public override Task<Stream> OpenReadAsync(
        string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!path.Equals("/hello.txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new VirtualFileSystemException(FileSystemError.NotFound);
        }

        return Task.FromResult<Stream>(new MemoryStream(Content, writable: false));
    }
}
```

For changing/generated content, report a byte length and ETag that match the read snapshot. Avoid generating different content between metadata lookup and opening the stream. Names must be unique ignoring case; enumeration yields direct children only.

## Adding writes and other operations

Override only the operations your storage supports:

| Override | What you implement |
| --- | --- |
| `BeginWriteAsync` | Return an `IVirtualWriteSession` with a writable staging stream. |
| `CreateDirectoryAsync` | Create a single directory after validating its parent. |
| `DeleteAsync` | Delete a file or a directory and its descendants. |
| `CopyAsync` | Honor overwrite and recursive/depth-zero behavior within your provider. |
| `MoveAsync` | Move content and properties; support case-only renames. |
| `GetPropertiesAsync` | Return detached XML values if you store custom properties. |
| `PatchPropertiesAsync` | Apply the entire set/remove batch atomically. |

By default, mutations throw `VirtualFileSystemException(NotSupported)`, returned to clients as HTTP 405. Property reads return an empty collection. Cancellation is checked before these defaults. The base class does not allocate storage or supply an implicit copy/move fallback.

A write session receives the entire new content through `Stream.WriteAsync`. The server flushes it, calls `CommitAsync` after a complete upload, and then disposes the session. Commit must publish the new content and metadata atomically. Disposing without commit must discard staged changes and close the stream. Existing file content must survive failed or cancelled uploads. The [in-memory sample's write session](../samples/VirtualWebDav.Sample/InMemoryFileSystem.cs) demonstrates this lifecycle; use disk or backend staging for large files.

Return a custom `Stream` if you need individual read/write callbacks. Streams and sessions are returned unchanged through the composite; it does not buffer their contents or take ownership of your provider. All asynchronous operations retain the original cancellation token.

Mounts are immutable after construction. Adding a provider means constructing a new composite before starting a server. The composite owns only its virtual root and routing table; mounted providers own their data and metadata. Copies/moves between mounts are deliberately unsupported, and mounted root folders themselves are protected.
