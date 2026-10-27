using System.Text;
using System.Text.Json;
using TermLume.DesktopBackend;
using TermLume.Shared.Application;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length != 2 || args[0] != "--data-dir" || !Path.IsPathFullyQualified(args[1]))
{
    Console.Error.WriteLine("Usage: TermLume.DesktopBackend --data-dir <absolute-directory>");
    return 2;
}
using var dispatcher = new ThreadDispatcher();
var rpc = new DesktopRpc();
WorkspaceController controller;
try { controller = new WorkspaceController(Path.GetFullPath(args[1]), rpc, dispatcher); }
catch (Exception exception)
{
    rpc.Send(new { type = "fatal", error = exception.Message });
    return 1;
}
var requests = 0;
_ = Task.Run(async () =>
{
    try
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            if (line.Length > 2 * 1024 * 1024) break;
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var request = document.RootElement.Clone();
            if (request.TryGetProperty("type", out var type) && type.GetString() == "hostReply") { rpc.Complete(request); continue; }
            if (Interlocked.Increment(ref requests) > 256) { Interlocked.Decrement(ref requests); break; }
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var paths = request.GetProperty("op").GetString() == "sftpDropUpload"
                        ? request.GetProperty("nativePaths").Deserialize<string[]>() : null;
                    await controller.HandleAsync(request, paths);
                }
                catch (Exception exception)
                {
                    var id = request.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    rpc.Send(new { type = "reply", id, ok = false, error = exception.Message });
                }
                finally { Interlocked.Decrement(ref requests); }
            });
        }
    }
    catch (Exception) { }
    finally
    {
        rpc.Cancel();
        dispatcher.BeginInvoke(async () => { await controller.ShutdownAsync(); dispatcher.Stop(); });
    }
});
rpc.Send(new { type = "ready" });
dispatcher.Run();
return 0;
