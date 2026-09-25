using System.Text;
using VirtualWebDav;
using VirtualWebDav.Sample;

var provider = new InMemoryFileSystem();
await provider.CreateDirectoryAsync("/Documents", default);
await using (var write = await provider.BeginWriteAsync("/Documents/hello.txt", default))
{
    await write.Stream.WriteAsync(Encoding.UTF8.GetBytes("Hello from a virtual file system!\r\n"));
    await write.CommitAsync(default);
}
var port = args.Length > 0 ? int.Parse(args[0]) : 8085;
await using var server = await WebDavServer.StartAsync(provider, new() { Port = port });
Console.WriteLine($"WebDAV running at {server.ListeningUri}. Data is held in memory. Press Ctrl+C to stop.");
var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopped.TrySetResult(); };
await stopped.Task;
