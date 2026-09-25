using System.Runtime.CompilerServices;
using System.Xml.Linq;
using VirtualWebDav;

namespace VirtualWebDav.Sample;

/// <summary>Thread-safe demonstration storage. All data is lost when the process exits.</summary>
public sealed class InMemoryFileSystem : IVirtualFileSystem
{
    private sealed class Node(bool directory)
    {
        public bool Directory = directory;
        public byte[] Data = [];
        public DateTimeOffset Created = DateTimeOffset.UtcNow;
        public DateTimeOffset Modified = DateTimeOffset.UtcNow;
        public string Tag = "\"" + Guid.NewGuid().ToString("N") + "\"";
        public Dictionary<XName, XElement> Properties = [];
        public Node Clone() => new(Directory) { Data = Data.ToArray(), Properties = Properties.ToDictionary(p => p.Key, p => new XElement(p.Value)) };
        public void Touch() { Modified = DateTimeOffset.UtcNow; Tag = "\"" + Guid.NewGuid().ToString("N") + "\""; }
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Node> nodes = new(StringComparer.OrdinalIgnoreCase) { ["/"] = new(true) };
    private Node Required(string path) => nodes.TryGetValue(path, out var node) ? node : throw new VirtualFileSystemException(FileSystemError.NotFound);
    private void ParentExists(string path)
    {
        if (!nodes.TryGetValue(VirtualPath.Parent(path), out var parent) || !parent.Directory) throw new VirtualFileSystemException(FileSystemError.Conflict);
    }
    private static string Path(string path, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return VirtualPath.Normalize(path); }
    private static VirtualEntry Entry(string path, Node n) => new(path == "/" ? "" : path[(path.LastIndexOf('/') + 1)..], n.Directory, n.Directory ? 0 : n.Data.LongLength, n.Created, n.Modified, n.Tag);
    private void TouchParent(string path) { if (nodes.TryGetValue(VirtualPath.Parent(path), out var parent)) parent.Touch(); }

    public Task<VirtualEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
    {
        path = Path(path, cancellationToken);
        lock (gate)
        {
            var pair = nodes.FirstOrDefault(p => p.Key.Equals(path, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(pair.Value == null ? null : Entry(pair.Key, pair.Value));
        }
    }
    public async IAsyncEnumerable<VirtualEntry> EnumerateAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        path = Path(path, cancellationToken);
        VirtualEntry[] entries;
        lock (gate)
        {
            if (!Required(path).Directory) throw new VirtualFileSystemException(FileSystemError.Conflict);
            entries = nodes.Where(p => p.Key != "/" && VirtualPath.Parent(p.Key).Equals(path, StringComparison.OrdinalIgnoreCase)).Select(p => Entry(p.Key, p.Value)).ToArray();
        }
        foreach (var entry in entries) { cancellationToken.ThrowIfCancellationRequested(); yield return entry; }
        await Task.CompletedTask;
    }
    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        path = Path(path, cancellationToken);
        lock (gate)
        {
            var node = Required(path);
            if (node.Directory) throw new VirtualFileSystemException(FileSystemError.Conflict);
            return Task.FromResult<Stream>(new MemoryStream(node.Data, false));
        }
    }
    public Task<IVirtualWriteSession> BeginWriteAsync(string path, CancellationToken cancellationToken)
    {
        path = Path(path, cancellationToken);
        lock (gate)
        {
            ParentExists(path);
            if (nodes.TryGetValue(path, out var node) && node.Directory) throw new VirtualFileSystemException(FileSystemError.Conflict);
        }
        return Task.FromResult<IVirtualWriteSession>(new WriteSession(this, path));
    }
    private sealed class WriteSession(InMemoryFileSystem owner, string path) : IVirtualWriteSession
    {
        private readonly MemoryStream buffer = new();
        private bool completed;
        public Stream Stream => buffer;
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed) throw new InvalidOperationException("Session is completed.");
            var data = buffer.ToArray();
            lock (owner.gate)
            {
                owner.ParentExists(path);
                if (owner.nodes.TryGetValue(path, out var current) && current.Directory) throw new VirtualFileSystemException(FileSystemError.Conflict);
                var node = current ?? new Node(false);
                node.Data = data; node.Touch(); owner.nodes[path] = node; owner.TouchParent(path);
                completed = true;
            }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { completed = true; return buffer.DisposeAsync(); }
    }
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        path = Path(path, cancellationToken);
        lock (gate)
        {
            ParentExists(path);
            if (nodes.ContainsKey(path)) throw new VirtualFileSystemException(FileSystemError.AlreadyExists);
            nodes.Add(path, new(true)); TouchParent(path);
        }
        return Task.CompletedTask;
    }
    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        path = Path(path, cancellationToken);
        lock (gate)
        {
            if (path == "/") throw new VirtualFileSystemException(FileSystemError.Forbidden);
            Required(path);
            foreach (var key in nodes.Keys.Where(p => VirtualPath.Within(p, path)).ToArray()) nodes.Remove(key);
            TouchParent(path);
        }
        return Task.CompletedTask;
    }
    public Task CopyAsync(string source, string destination, bool overwrite, bool recursive, CancellationToken cancellationToken) => Transfer(source, destination, overwrite, recursive, false, cancellationToken);
    public Task MoveAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken) => Transfer(source, destination, overwrite, true, true, cancellationToken);
    private Task Transfer(string source, string destination, bool overwrite, bool recursive, bool move, CancellationToken ct)
    {
        source = Path(source, ct); destination = Path(destination, ct);
        lock (gate)
        {
            Required(source); ParentExists(destination);
            bool caseRename = move && destination.Equals(source, StringComparison.OrdinalIgnoreCase) && destination != source;
            if (!caseRename && (VirtualPath.Within(destination, source) || VirtualPath.Within(source, destination))) throw new VirtualFileSystemException(FileSystemError.Forbidden);
            if (nodes.ContainsKey(destination) && !overwrite) throw new VirtualFileSystemException(FileSystemError.AlreadyExists);
            var selected = nodes.Where(p => p.Key.Equals(source, StringComparison.OrdinalIgnoreCase) || recursive && VirtualPath.Within(p.Key, source)).ToArray();
            var replacement = selected.Select(p => KeyValuePair.Create(destination + p.Key[source.Length..], move ? p.Value : p.Value.Clone())).ToArray();
            foreach (var key in nodes.Keys.Where(p => VirtualPath.Within(p, destination)).ToArray()) nodes.Remove(key);
            if (move) foreach (var item in selected) nodes.Remove(item.Key);
            foreach (var item in replacement) nodes.Add(item.Key, item.Value);
            if (move) TouchParent(source);
            TouchParent(destination);
        }
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<XElement>> GetPropertiesAsync(string path, CancellationToken cancellationToken)
    {
        path = Path(path, cancellationToken);
        lock (gate) return Task.FromResult<IReadOnlyList<XElement>>(Required(path).Properties.Values.Select(p => new XElement(p)).ToArray());
    }
    public Task PatchPropertiesAsync(string path, IReadOnlyList<XElement> set, IReadOnlyList<XName> remove, CancellationToken cancellationToken)
    {
        path = Path(path, cancellationToken);
        lock (gate)
        {
            var node = Required(path);
            var updated = node.Properties.ToDictionary(p => p.Key, p => new XElement(p.Value));
            foreach (var name in remove) updated.Remove(name);
            foreach (var property in set) updated[property.Name] = new XElement(property);
            node.Properties = updated; node.Touch();
        }
        return Task.CompletedTask;
    }
}
