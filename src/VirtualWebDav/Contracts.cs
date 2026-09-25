using System.Xml.Linq;

namespace VirtualWebDav;

public sealed record VirtualEntry(string Name, bool IsDirectory, long Length,
    DateTimeOffset Created, DateTimeOffset Modified, string ETag);

public interface IVirtualWriteSession : IAsyncDisposable
{
    Stream Stream { get; }
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IVirtualFileSystem
{
    Task<VirtualEntry?> GetEntryAsync(string path, CancellationToken cancellationToken);
    IAsyncEnumerable<VirtualEntry> EnumerateAsync(string path, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);
    Task<IVirtualWriteSession> BeginWriteAsync(string path, CancellationToken cancellationToken);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);
    Task DeleteAsync(string path, CancellationToken cancellationToken);
    Task CopyAsync(string source, string destination, bool overwrite, bool recursive, CancellationToken cancellationToken);
    Task MoveAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken);
    Task<IReadOnlyList<XElement>> GetPropertiesAsync(string path, CancellationToken cancellationToken);
    Task PatchPropertiesAsync(string path, IReadOnlyList<XElement> set, IReadOnlyList<XName> remove, CancellationToken cancellationToken);
}

public enum FileSystemError { NotFound, Forbidden, Conflict, AlreadyExists, InsufficientStorage, NotSupported }

public sealed class VirtualFileSystemException(FileSystemError error) : Exception(error.ToString())
{
    public FileSystemError Error { get; } = error;
}

public sealed class WebDavServerOptions
{
    public int Port { get; init; } = 8085;
    public int MaxXmlBytes { get; init; } = 1024 * 1024;
    public TimeSpan MaxLockTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

public static class VirtualPath
{
    public static string Normalize(string path)
    {
        if (!path.StartsWith('/') || path.Contains('\\') || path.Contains('\0')) throw new ArgumentException("Invalid virtual path.");
        if (path == "/") return path;
        var segments = (path.EndsWith('/') ? path[..^1] : path).Split('/').Skip(1).ToArray();
        foreach (var s in segments)
        {
            if (s.Length == 0 || s is "." or ".." || s.EndsWith(' ') || s.EndsWith('.') ||
                s.Any(c => c < 32 || "<>:\"|?*".Contains(c))) throw new ArgumentException("Invalid virtual path.");
            var stem = s.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9'))
                throw new ArgumentException("Reserved Windows name.");
        }
        return "/" + string.Join('/', segments);
    }
    public static string Parent(string path) => path[..Math.Max(1, path.LastIndexOf('/'))];
    public static bool Within(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent == "/" ? "/" : parent + "/", StringComparison.OrdinalIgnoreCase);
    public static string Href(string path, bool directory) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString)) + (directory && path != "/" ? "/" : "");
}
