using System.Text.Json;
using TermLume.Shared.Abstractions;
using TermLume.Shared.Application;
using TermLume.Shared.Models;
using TermLume.Shared.Services;

internal static class WorkspaceControllerTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var dataDirectory = Path.Combine(root, "data");
        var original = new Workspace
        {
            Groups = ["Existing"],
            Hosts = [new HostProfile { Name = "Existing host", Address = "127.0.0.1", Port = 9, Group = "Existing" }],
            Snippets = [new Snippet { Name = "Existing snippet", Command = "echo existing" }]
        };
        var store = new WorkspaceStore(dataDirectory);
        store.Save(original);
        var host = new RecordingHost();
        var controller = new WorkspaceController(dataDirectory, host, new ImmediateDispatcher());
        async Task<JsonElement> Call(string operation, object? payload = null, string? requestId = null)
        {
            var id = requestId ?? Guid.NewGuid().ToString("N");
            await controller.HandleAsync(JsonSerializer.SerializeToElement(new { id, op = operation, payload = payload ?? new { } }, WorkspaceStore.JsonOptions));
            return host.Messages.Last(message => message.GetProperty("type").GetString() == "reply" && message.GetProperty("id").GetString() == id);
        }
        static bool Ok(JsonElement reply) => reply.GetProperty("ok").GetBoolean();
        static JsonElement Result(JsonElement reply) => reply.GetProperty("result");

        check(typeof(WorkspaceController).Assembly == typeof(WorkspaceStore).Assembly && typeof(Workspace).Assembly == typeof(WorkspaceStore).Assembly,
            "Models, services and controller are supplied by one Shared assembly");
        check(!typeof(WorkspaceController).Assembly.GetReferencedAssemblies().Any(assembly =>
            assembly.Name is "PresentationFramework" or "WindowsBase" or "Microsoft.Web.WebView2.Core" or "TermLume.DesktopBackend" or "TermLume"),
            "Shared does not reference desktop shells or platform transports");
        var loaded = Result(await Call("workspace"));
        check(loaded.GetProperty("hosts")[0].GetProperty("id").GetString() == original.Hosts[0].Id &&
            loaded.GetProperty("snippets")[0].GetProperty("id").GetString() == original.Snippets[0].Id,
            "Shared controller opens existing workspace identifiers without migration");
        check(Ok(await Call("saveGroup", new { name = " New group " })) && store.Load().Groups.Contains("New group"),
            "Group creation uses shared validation and persistence");
        check(!Ok(await Call("saveGroup", new { name = "New group" })), "Duplicate groups are rejected by the shared controller");
        check(Ok(await Call("saveHost", new { name = "New host", address = "127.0.0.1", port = 9, username = "audit", group = "New group" })),
            "Host creation is handled by the shared controller");
        var savedHost = store.Load().Hosts.Single(item => item.Name == "New host");
        check(!Ok(await Call("saveHost", new { name = "Invalid", address = "ssh://invalid", username = "audit" })) && store.Load().Hosts.Count == 2,
            "Invalid host input cannot enter saved data");
        check(Ok(await Call("favorite", new { id = savedHost.Id })) && store.Load().Hosts.Single(item => item.Id == savedHost.Id).Favorite,
            "Favorites persist through the shared controller");
        check(Ok(await Call("saveSnippet", new { name = "New snippet", command = "echo test" })), "Snippet creation uses the shared controller");
        var snippet = store.Load().Snippets.Single(item => item.Name == "New snippet");
        check(!Ok(await Call("saveSnippet", new { name = "Invalid", command = new string('x', 20001) })), "Shared snippet validation keeps command size limits");
        check(Ok(await Call("deleteSnippet", new { id = snippet.Id })) && store.Load().Snippets.Count == 1, "Snippet deletion preserves unrelated existing snippets");
        check(Ok(await Call("preferences", new Preferences { Language = "en", FontSize = 18 })) && Localization.Language == "en" && store.Load().Preferences.FontSize == 18,
            "Preferences and native language change through one implementation");
        check(!Ok(await Call("preferences", new Preferences { Language = "invalid" })) && Localization.Language == "en", "Invalid preferences do not change native language");

        var cancelled = await Call("connect", new { hostId = savedHost.Id, sessionId = Guid.NewGuid().ToString("N") });
        check(!Ok(cancelled) && controller.Status.Ssh == 0 && host.Requests.Last() == "credential", "Cancelled native credentials never create an SSH session");
        check(!Ok(await Call("sftpConnect", new { hostId = savedHost.Id })) && controller.Status.Sftp == 0, "Cancelled credentials never create an SFTP session");
        var credentialRequests = host.Requests.Count;
        check(!Ok(await Call("connect", new { hostId = savedHost.Id, sessionId = "invalid" })) && host.Requests.Count == credentialRequests,
            "Invalid session identifiers are rejected before native credential prompts");

        var localDirectory = Path.Combine(root, "files");
        Directory.CreateDirectory(localDirectory);
        File.WriteAllText(Path.Combine(localDirectory, "synthetic.txt"), "synthetic contents");
        host.Replies["pickFolder"] = localDirectory;
        var listing = await Call("sftpPickLocal");
        check(Ok(listing) && Result(listing).GetProperty("entries").EnumerateArray().Any(entry => entry.GetProperty("name").GetString() == "synthetic.txt"),
            "Shared local browsing uses the folder returned by the native host");
        check(!Ok(await Call("sftpLocalList", new { path = root })), "Shared local browsing cannot escape the selected folder");
        host.Replies["pickFolder"] = null;
        check(Result(await Call("sftpPickLocal")).ValueKind == JsonValueKind.Null && Ok(await Call("sftpLocalList", new { path = localDirectory })),
            "Cancelling a folder picker preserves the previous browsing scope");

        var logIds = Result(await Call("logs")).GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("id").GetString()!).ToArray();
        var beforeExport = File.ReadAllBytes(Path.Combine(dataDirectory, "workspace.json"));
        host.Replies["exportLogs"] = Path.Combine(dataDirectory, "WORKSPACE.JSON");
        check(!Ok(await Call("exportLogs", new { ids = logIds })) && beforeExport.SequenceEqual(File.ReadAllBytes(Path.Combine(dataDirectory, "workspace.json"))),
            "Shared export validation rejects case variants of the workspace path");
        host.Replies["exportLogs"] = Path.Combine(root, "export.json");
        check(Ok(await Call("exportLogs", new { ids = logIds })) && File.Exists(Path.Combine(root, "export.json")), "Log export requests a native path and writes a separate copy");
        check(host.Messages.Any(message => message.GetProperty("type").GetString() == "activityChanged"), "Shared activity events reach the platform host");
        check(!Ok(await Call("unknownOperation")), "Unknown operations produce a structured error reply");
        check(!Ok(await Call("workspace", requestId: new string('x', 101))), "Oversized request identifiers produce a structured error reply");
        check(Ok(await Call("deleteHost", new { id = savedHost.Id })) && store.Load().Hosts.Single().Id == original.Hosts[0].Id,
            "Host deletion preserves existing workspace records");

        Directory.CreateDirectory(Path.Combine(dataDirectory, "workspace.json.tmp"));
        check(!Ok(await Call("preferences", new Preferences { Language = "zh-CN" })) && Localization.Language == "en" &&
            Result(await Call("workspace")).GetProperty("preferences").GetProperty("language").GetString() == "en",
            "Failed preference persistence rolls back in-memory preferences and native language");
        check(await controller.ShutdownAsync() && await controller.ShutdownAsync(), "Shared shutdown is safe to request more than once");
        check(!Ok(await Call("saveGroup", new { name = "After shutdown" })), "Shared shutdown prevents later workspace mutations");
        var restoredHost = new RecordingHost();
        var restored = new WorkspaceController(dataDirectory, restoredHost, new ImmediateDispatcher());
        check(store.Load().Hosts.Single().Id == original.Hosts[0].Id && store.Load().Preferences.Language == "en", "Shared shutdown preserves existing on-disk data");
        await restored.ShutdownAsync();
        Localization.Language = "zh-CN";
    }

    private sealed class RecordingHost : IWorkspaceHost
    {
        public List<JsonElement> Messages { get; } = [];
        public List<string> Requests { get; } = [];
        public Dictionary<string, object?> Replies { get; } = [];
        public void Send(object message) => Messages.Add(JsonSerializer.SerializeToElement(message, WorkspaceStore.JsonOptions));
        public Task<T?> RequestAsync<T>(string operation, object? payload = null)
        {
            Requests.Add(operation);
            var value = Replies.GetValueOrDefault(operation);
            return Task.FromResult(value is null ? default : (T?)value);
        }
    }

    private sealed class ImmediateDispatcher : IWorkspaceDispatcher
    {
        public void BeginInvoke(Action action) => action();
        public void Invoke(Action action) => action();
        public T Invoke<T>(Func<T> action) => action();
    }
}
