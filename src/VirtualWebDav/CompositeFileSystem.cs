using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace VirtualWebDav;
/// <summary>Routes fixed top-level folders to independent filesystem providers.</summary>
/// <remarks>Mount registrations are copied. Providers and returned content retain their original ownership contracts.</remarks>
public sealed class CompositeFileSystem : IVirtualFileSystem
{
    private sealed record Mount(string Name, IVirtualFileSystem Provider);
    private sealed record Route(Mount Mount, string Path);
    private readonly Dictionary<string, Mount> mounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly VirtualEntry root;
    /// <summary>Register single-segment paths such as /FolderA. Providers must expose a directory at their root.</summary>
    public CompositeFileSystem(IReadOnlyDictionary<string, IVirtualFileSystem> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration.Value);
            string path = VirtualPath.Normalize(registration.Key);
            if (path == "/" || path.IndexOf('/', 1) >= 0)
            {
                throw new ArgumentException("Mount paths must be single top-level folders.", nameof(registrations));
            }

            var mount = new Mount(path[1..], registration.Value);
            if (!mounts.TryAdd(mount.Name, mount))
            {
                throw new ArgumentException("Mount names must be unique ignoring case.", nameof(registrations));
            }
        }

        var now = DateTimeOffset.UtcNow;
        root = new VirtualEntry("", true, 0, now, now, "\"" + Guid.NewGuid().ToString("N") + "\"");
    }

    private Route? Resolve(string path)
    {
        int separator = path.IndexOf('/', 1);
        string name = separator < 0 ? path[1..] : path[1..separator];
        return mounts.TryGetValue(name, out var mount) ? new Route(mount, separator < 0 ? "/" : path[separator..]) : null;
    }

    private static string Normalize(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return VirtualPath.Normalize(path);
    }

    private static async Task<VirtualEntry> ValidateRootAsync(Mount mount, CancellationToken cancellationToken)
    {
        var entry = await mount.Provider.GetEntryAsync("/", cancellationToken);
        if (entry == null || !entry.IsDirectory)
        {
            throw new InvalidOperationException($"Provider mounted at /{mount.Name} must expose a directory at /.");
        }

        return entry with
        {
            Name = mount.Name
        };
    }

    private Route RequireRoute(string path)
    {
        return Resolve(path) ?? throw new VirtualFileSystemException(FileSystemError.NotFound);
    }

    private static void ProtectRoot(string path, Route? route)
    {
        if (path == "/" || route?.Path == "/")
        {
            throw new VirtualFileSystemException(FileSystemError.Forbidden);
        }
    }

    private async Task<Route> RouteAsync(string path, CancellationToken cancellationToken, bool protectMountRoot = false)
    {
        path = Normalize(path, cancellationToken);
        var route = Resolve(path);
        if (protectMountRoot)
        {
            ProtectRoot(path, route);
        }

        route ??= RequireRoute(path);
        await ValidateRootAsync(route.Mount, cancellationToken);
        return route;
    }

    public async Task<VirtualEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path, cancellationToken);
        if (path == "/")
        {
            return root;
        }

        var route = Resolve(path);
        if (route == null)
        {
            return null;
        }

        var mountRoot = await ValidateRootAsync(route.Mount, cancellationToken);
        return route.Path == "/" ? mountRoot : await route.Mount.Provider.GetEntryAsync(route.Path, cancellationToken);
    }

    public async IAsyncEnumerable<VirtualEntry> EnumerateAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        path = Normalize(path, cancellationToken);
        if (path == "/")
        {
            foreach (var mount in mounts.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return await ValidateRootAsync(mount, cancellationToken);
            }

            yield break;
        }

        var route = await RouteAsync(path, cancellationToken);
        await foreach (var entry in route.Mount.Provider.EnumerateAsync(route.Path, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return entry;
        }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var route = await RouteAsync(path, cancellationToken, protectMountRoot: true);
        return await route.Mount.Provider.OpenReadAsync(route.Path, cancellationToken);
    }

    public async Task<IVirtualWriteSession> BeginWriteAsync(string path, CancellationToken cancellationToken)
    {
        var route = await RouteAsync(path, cancellationToken, protectMountRoot: true);
        return await route.Mount.Provider.BeginWriteAsync(route.Path, cancellationToken);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var route = await RouteAsync(path, cancellationToken, protectMountRoot: true);
        await route.Mount.Provider.CreateDirectoryAsync(route.Path, cancellationToken);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var route = await RouteAsync(path, cancellationToken, protectMountRoot: true);
        await route.Mount.Provider.DeleteAsync(route.Path, cancellationToken);
    }

    private async Task<(Route Source, Route Destination)> TransferRoutesAsync(string source, string destination, CancellationToken cancellationToken)
    {
        source = Normalize(source, cancellationToken);
        destination = Normalize(destination, cancellationToken);
        var from = Resolve(source);
        var to = Resolve(destination);
        ProtectRoot(source, from);
        ProtectRoot(destination, to);
        from ??= RequireRoute(source);
        to ??= RequireRoute(destination);
        // Decide this before even looking up provider roots: rejected transfers have no callbacks.
        if (!ReferenceEquals(from.Mount, to.Mount))
        {
            throw new VirtualFileSystemException(FileSystemError.NotSupported);
        }

        await ValidateRootAsync(from.Mount, cancellationToken);
        return (from, to);
    }

    public async Task CopyAsync(string source, string destination, bool overwrite, bool recursive, CancellationToken cancellationToken)
    {
        var routes = await TransferRoutesAsync(source, destination, cancellationToken);
        await routes.Source.Mount.Provider.CopyAsync(routes.Source.Path, routes.Destination.Path, overwrite, recursive, cancellationToken);
    }

    public async Task MoveAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        var routes = await TransferRoutesAsync(source, destination, cancellationToken);
        await routes.Source.Mount.Provider.MoveAsync(routes.Source.Path, routes.Destination.Path, overwrite, cancellationToken);
    }

    public async Task<IReadOnlyList<XElement>> GetPropertiesAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path, cancellationToken);
        if (path == "/")
        {
            return Array.Empty<XElement>();
        }

        var route = await RouteAsync(path, cancellationToken);
        return await route.Mount.Provider.GetPropertiesAsync(route.Path, cancellationToken);
    }

    public async Task PatchPropertiesAsync(string path, IReadOnlyList<XElement> set, IReadOnlyList<XName> remove, CancellationToken cancellationToken)
    {
        path = Normalize(path, cancellationToken);
        if (path == "/")
        {
            throw new VirtualFileSystemException(FileSystemError.Forbidden);
        }

        var route = await RouteAsync(path, cancellationToken);
        await route.Mount.Provider.PatchPropertiesAsync(route.Path, set, remove, cancellationToken);
    }
}
