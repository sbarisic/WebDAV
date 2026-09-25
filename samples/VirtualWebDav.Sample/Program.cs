using System.Text;

namespace VirtualWebDav.Sample;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var folderA = await CreateFileSystemAsync("Hello from FolderA!\r\n");
        var folderB = await CreateFileSystemAsync("Hello from FolderB!\r\n");

        var mounts = new Dictionary<string, IVirtualFileSystem>
        {
            ["/FolderA"] = folderA,
            ["/FolderB"] = folderB
        };

        var fileSystem = new CompositeFileSystem(mounts);

        int port = args.Length > 0 ? int.Parse(args[0]) : 8085;
        var options = new WebDavServerOptions
        {
            Port = port
        };

        await using var server = await WebDavServer.StartAsync(
            fileSystem,
            options);

        Console.WriteLine(
            $"WebDAV running at {server.ListeningUri}. " +
            "Data is held in memory. Press Ctrl+C to stop.");

        var stopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopped.TrySetResult();
        };

        await stopped.Task;
    }

    private static async Task<InMemoryFileSystem> CreateFileSystemAsync(string greeting)
    {
        var provider = new InMemoryFileSystem();

        await provider.CreateDirectoryAsync("/Documents", default);

        await using var write = await provider.BeginWriteAsync(
            "/Documents/hello.txt",
            default);

        await write.Stream.WriteAsync(Encoding.UTF8.GetBytes(greeting));
        await write.CommitAsync(default);

        return provider;
    }
}
