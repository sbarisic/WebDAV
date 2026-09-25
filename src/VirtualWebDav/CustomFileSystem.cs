using System.Xml.Linq;

namespace VirtualWebDav;
/// <summary>Base for provider-defined filesystems. Implement reads and override only the mutations you support.</summary>
/// <remarks>Paths are normalized and relative to this provider's root, including when mounted.
/// Providers remain caller-owned. Implementations must honor cancellation before publishing changes.</remarks>
public abstract class CustomFileSystem : IVirtualFileSystem
{
    /// <summary>Look up provider-relative metadata, or return null if absent. Honor cancellation.</summary>
    public abstract Task<VirtualEntry?> GetEntryAsync(string path, CancellationToken cancellationToken);
    /// <summary>Enumerate direct children of a provider-relative directory, honoring cancellation.</summary>
    public abstract IAsyncEnumerable<VirtualEntry> EnumerateAsync(string path, CancellationToken cancellationToken);
    /// <summary>Open a stable read snapshot. The server owns and disposes the returned stream. Honor cancellation.</summary>
    public abstract Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);
    /// <summary>Stage a provider-relative create/replace. The session owns its stream and must abort on disposal unless committed.</summary>
    /// <remarks>Override to support writes. Commit must publish atomically and honor cancellation before publishing.</remarks>
    public virtual Task<IVirtualWriteSession> BeginWriteAsync(string path, CancellationToken cancellationToken)
    {
        throw Unsupported(cancellationToken);
    }

    /// <summary>Create one provider-relative directory. By default rejects the operation after checking cancellation.</summary>
    public virtual Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        throw Unsupported(cancellationToken);
    }

    /// <summary>Delete a provider-relative entry recursively. By default rejects after checking cancellation.</summary>
    public virtual Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        throw Unsupported(cancellationToken);
    }

    /// <summary>Copy between provider-relative paths, honoring overwrite and recursion. By default rejects after checking cancellation.</summary>
    public virtual Task CopyAsync(string source, string destination, bool overwrite, bool recursive, CancellationToken cancellationToken)
    {
        throw Unsupported(cancellationToken);
    }

    /// <summary>Move between provider-relative paths, including descendants. By default rejects after checking cancellation.</summary>
    public virtual Task MoveAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        throw Unsupported(cancellationToken);
    }

    /// <summary>Read detached custom-property XML for a provider-relative entry. Defaults to empty after checking cancellation.</summary>
    public virtual Task<IReadOnlyList<XElement>> GetPropertiesAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<XElement>>(Array.Empty<XElement>());
    }

    /// <summary>Atomically update custom properties at a provider-relative path. By default rejects after checking cancellation.</summary>
    public virtual Task PatchPropertiesAsync(string path, IReadOnlyList<XElement> set, IReadOnlyList<XName> remove, CancellationToken cancellationToken)
    {
        throw Unsupported(cancellationToken);
    }

    private static VirtualFileSystemException Unsupported(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new VirtualFileSystemException(FileSystemError.NotSupported);
    }
}
