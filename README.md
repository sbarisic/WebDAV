# VirtualWebDav

A .NET 8 library that embeds a WebDAV server for a provider-defined virtual filesystem. Map it as a Windows drive while your application supplies directory listings, metadata, and file content. No physical backing directory is required.

The server binds **only to localhost**, over anonymous HTTP. Other users and processes on the same machine can access it. It is not intended for LAN or internet hosting.

## Try it

Install a .NET 8 SDK (or a newer SDK plus the .NET 8 ASP.NET Core runtime), then:

```powershell
dotnet run --project samples/VirtualWebDav.Sample
```

The sample serves `http://localhost:8085/`, with two independent in-memory providers:

```text
/
├── FolderA/Documents/hello.txt   ("Hello from FolderA!")
└── FolderB/Documents/hello.txt   ("Hello from FolderB!")
```

Its data disappears on exit. Pass a different fixed port after `--`, for example `-- 8090`. Stop with Ctrl+C.

Map an unused Windows drive letter:

```powershell
net use W: http://localhost:8085/ /persistent:no
# Alternative UNC spelling:
# net use W: \\localhost@8085\DavWWWRoot /persistent:no

# Disconnect before stopping the sample:
net use W: /delete
```

Windows Explorer's **Map network drive** also accepts the UNC spelling. Windows WebClient must be installed and running; mapping may start it automatically. If needed, explicitly run `Start-Service WebClient` from an elevated Windows PowerShell session. The library never configures services, registry settings, or drive mappings.

## Embed in another project

Add a project reference to `src/VirtualWebDav/VirtualWebDav.csproj`, or build a local NuGet package:

```powershell
dotnet pack src/VirtualWebDav/VirtualWebDav.csproj -c Release -o artifacts
```

The package requires the .NET 8 ASP.NET Core shared framework. It is not published on nuget.org by this project.

```csharp
using VirtualWebDav;

IVirtualFileSystem provider = new MyVirtualFileSystem();
await using var server = await WebDavServer.StartAsync(
    provider,
    new WebDavServerOptions { Port = 8085 },
    cancellationToken);

Console.WriteLine(server.ListeningUri);
// Keep your application alive while clients use the drive.
// Disposing the server stops accepting requests and drains active requests.
```

Derive from `CustomFileSystem`, or implement `IVirtualFileSystem` directly; both work with the existing server API. See the complete [in-memory sample](samples/VirtualWebDav.Sample/InMemoryFileSystem.cs) and [custom-provider tutorial](docs/custom-file-systems.md). A provider can generate folders and files dynamically. The base class requires just metadata lookup, enumeration, and reading; write operations are virtual and reject requests until overridden.

### Mount several providers

Inside your `Program.Main`, construct a composite and pass it to the server:

```csharp
var fileSystem = new CompositeFileSystem(
    new Dictionary<string, IVirtualFileSystem>
    {
        ["/FolderA"] = new MyFileSystem(),
        ["/FolderB"] = new AnotherFileSystem()
    });

await using var server = await WebDavServer.StartAsync(
    fileSystem,
    new WebDavServerOptions { Port = 8085 });
```

Here `MyFileSystem` and `AnotherFileSystem` are your own classes derived from `CustomFileSystem`. The composite lists mount names at `/` and translates `/FolderA/report.txt` into `/report.txt` for that provider. Mount paths are fixed, single top-level folders. Registration is copied at construction; paths match ignoring case while retaining display spelling. Nested mounts and duplicate names are rejected.

Each provider must expose an existing directory at `/`; invalid roots surface as configuration errors (HTTP 500), not hidden mounts. Unknown mounts return not-found errors. The composite root and mount roots cannot be created, deleted, overwritten, copied, or moved. Mount-root custom properties still go to the provider; the synthetic root has empty, read-only custom properties.

Copies and moves within a mount delegate normally. Cross-mount copies/moves return HTTP 405 without performing any transfer, even when two mounts reference the same provider instance. Providers remain caller-owned. WebDAV locks use the full external path; mounting the same mutable storage at multiple paths does not coordinate locks between aliases.

| Provider operation | Responsibility |
| --- | --- |
| `GetEntryAsync` | Return metadata, or null if absent. Root `/` must exist as a directory. |
| `EnumerateAsync` | Yield direct children only, with unique names ignoring case. |
| `OpenReadAsync` | Return a readable stream representing a stable content snapshot. |
| `BeginWriteAsync` | Return a staged create/replace session. Never truncate existing content at this point. |
| `CreateDirectoryAsync` | Create one directory; require an existing parent. |
| `DeleteAsync` | Delete an entry, recursively for a directory. |
| `CopyAsync` | Copy metadata/content/custom properties; honor overwrite and recursive/depth-zero behavior. |
| `MoveAsync` | Move an entry and its descendants, preserving custom properties; support case-only renames. |
| `GetPropertiesAsync` | Return detached XML elements for custom properties. |
| `PatchPropertiesAsync` | Apply the complete set/remove batch atomically. |

### Provider contract

- Paths are decoded, normalized, root-relative strings: `/`, `/Documents`, `/Documents/file.txt`. They are **not OS paths**. Match case-insensitively while preserving stored spelling. Entry names are single segments; the root name is empty. Reject duplicate names ignoring case. `VirtualPath` supplies common helpers.
- File metadata must report the exact byte length, UTC-compatible timestamps, and a quoted strong ETag such as `"revision-42"`. Change the ETag when content changes. Directories report length zero. Keep metadata and read snapshots consistent if your backend changes outside this server.
- `OpenReadAsync` transfers stream ownership to the server, which disposes it even on failed downloads. Seekable streams enable single byte ranges; non-seekable streams return a full download. Implement a custom `Stream` to receive individual read calls.
- `IVirtualWriteSession.Stream` receives upload chunks. `CommitAsync` is called only after the complete request body is copied and flushed. Publish the staged content atomically, including the new size, timestamp and ETag. Disposal without commit must abort and free staging resources. The session owns and disposes its stream. Honor cancellation before publishing; once committed, do not report cancellation as an unsuccessful commit.
- PUT replaces the entire file; it is not a random-offset write. Windows can send a separate zero-byte PUT before uploading content. Each complete PUT is an independent commit.
- Create/replace/copy/move must validate parents and overwrite conditions before changing storage. `CopyAsync(recursive: false)` on a directory creates an empty destination directory with copied properties. Delete and move include all descendants. Do not change anything when validation fails. Avoid partially completed mutations; the initial interface cannot report per-descendant partial failures.
- Custom properties are keyed by expanded XML name. Return copies and apply updates atomically. The server protects properties in `DAV:`; Windows properties in `urn:schemas-microsoft-com:` are provider-owned custom properties. If your backend needs those properties to affect native metadata, implement that translation in your provider.
- All methods receive cancellation tokens. Providers own their lifetime and are not disposed by the server. One server serializes requests, including transfers, to keep its checks and mutations consistent. A provider shared with other servers or modified externally must implement its own synchronization and consistency controls. The sample is thread-safe.
- Throw `VirtualFileSystemException` with `NotFound`, `Forbidden`, `Conflict`, `AlreadyExists`, `InsufficientStorage`, or `NotSupported` for HTTP 404, 403, 409, 412, 507, or 405. Unexpected exceptions become 500 and are logged locally; exception details are not returned to clients.

Callbacks describe HTTP requests and content-stream activity. Windows caching can combine or suppress operations, so these are not application file-open/close or NTFS callbacks.

## Protocol support and boundaries

Implemented methods: OPTIONS, PROPFIND, PROPPATCH, GET, HEAD, PUT, MKCOL, DELETE, COPY, MOVE, LOCK, UNLOCK. Includes escaped hrefs, multistatus responses, ETags, conditional writes, last-modified checks, recursive operations, and exclusive write locks with expiry, refresh, and collection depth. Locks are in-process and disappear on restart. Persisted file content and properties belong to your provider.

- PROPFIND supports depth 0 and 1. Omitted/infinite depth returns the explicit `propfind-finite-depth` error.
- GET supports one byte range for seekable streams. Multiple/unsupported ranges fall back to a full response. HEAD reports full representation metadata.
- LOCK supports exclusive write locks, not shared locks. Locking a missing file commits an empty file and returns 201. Default/max lock timeout is 30 minutes, configurable.
- File transfers use 64 KiB buffers. XML bodies are limited to 1 MiB by default, with DTDs and external entities disabled. Listings/custom properties are materialized in memory, so very large directories have metadata costs.
- Host and remote-address validation restrict access to the configured loopback server. Requests carrying browser Origin or Fetch Metadata headers are rejected. There is no CORS support, authentication, HTTPS, middleware integration, or automatic drive mounting.
- Requests are serialized per server; a slow upload/download delays other operations. This initial implementation prioritizes predictable behavior for local use over concurrent transfer throughput.
- NTFS ACLs, alternate streams, filesystem notifications, persistence, and full Office compatibility are outside this release.

Protocol reference: [RFC 4918](https://www.rfc-editor.org/rfc/rfc4918).

## Windows limitations

Windows WebClient has independent timeout, listing-size, cache, and file-transfer limits. The validation machine had `FileSizeLimitInBytes = 50000000`; larger transfers through its mapped drive may fail even when direct HTTP succeeds. No registry settings are changed by this project. See [Microsoft's WebDAV redirector documentation](https://learn.microsoft.com/en-us/iis/publish/using-webdav/using-the-webdav-redirector) for optional system configuration and troubleshooting. Anonymous localhost HTTP does not require weakening Basic authentication settings.

PowerShell 7 / modern .NET directory enumeration can expose trailing null characters from Windows WebDAV, causing recursive enumeration errors. This was reproduced locally; Windows PowerShell 5.1 and native `dir` enumerated correctly. Use `powershell.exe` for the supplied smoke test. See [dotnet/runtime issue 46723](https://github.com/dotnet/runtime/issues/46723).

## Build and verify

```powershell
dotnet test VirtualWebDav.sln -c Release
dotnet pack src/VirtualWebDav/VirtualWebDav.csproj -c Release --no-build -o artifacts

# Opt-in mapped-drive test, with the sample already running:
powershell.exe -NoProfile -File scripts/Test-WindowsDrive.ps1 -WritableSubfolder FolderA -OtherSubfolder FolderB
```

The Windows script uses an unused drive letter, creates a unique test folder inside the selected writable subfolder, verifies operations and reconnects, then cleans up its folder and mapping. The optional other subfolder is checked for independent access and unchanged listings. Omit both subfolder arguments when testing a single writable provider at `/`. Subfolder arguments accept single names containing letters, digits, underscores, or hyphens. The script does not change service startup settings or registry configuration and restores the network-mapping persistence preference. CI runs protocol tests on Windows and Linux using .NET 8 and produces a NuGet artifact. See [validation results](docs/validation.md) for local acceptance evidence.
