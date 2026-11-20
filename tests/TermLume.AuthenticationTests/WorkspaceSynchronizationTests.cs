using System.Text.Json;
using TermLume.Shared.Abstractions;
using TermLume.Shared.Application;
using TermLume.Shared.Models;
using TermLume.Shared.Services;

static class WorkspaceSynchronizationTests
{
    public static async Task RunAsync(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, "synthetic-key");
        File.WriteAllText(keyPath, "test-key-not-a-real-private-key");
        var legacy = new Workspace
        {
            Groups = ["Legacy"], Hosts = [new() { Name = "Legacy host", Address = "legacy.example.test", Group = "Legacy", AuthType = "key", PrivateKeyPath = keyPath }],
            Snippets = [new() { Name = "Legacy snippet", Command = "echo legacy" }],
            KnownHosts = new() { ["legacy.example.test:22:ssh-ed25519"] = "SHA256:local-only" }
        };
        new WorkspaceStore(directory).Save(legacy);
        var original = File.ReadAllText(Path.Combine(directory, "workspace.json"));
        using var authentication = new Authentication();
        var synchronization = new Synchronization(authentication);
        var host = new Host();
        var controller = new WorkspaceController(directory, host, new Dispatcher(), authentication, synchronization);
        async Task<JsonElement> Call(string operation, object? payload = null) => await Send(controller, host, operation, payload);
        try
        {
            check((await Call("workspace")).GetProperty("result").GetProperty("hosts").GetArrayLength() == 0, "Legacy hosts are not exposed automatically to a signed-in account");
            var alphaDirectory = (await Call("workspaceSync")).GetProperty("result").GetProperty("dataDirectory").GetString()!;
            check((await Call("workspaceSync")).GetProperty("result").GetProperty("legacyImportAvailable").GetBoolean(), "A legacy workspace can be imported explicitly");
            synchronization.FailImport = true;
            check(!(await Call("importLegacyWorkspace")).GetProperty("ok").GetBoolean(), "Import failure is reported without discarding the source");
            using (var claim = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "workspace-import-owner.json"))))
                check(!claim.RootElement.GetProperty("completed").GetBoolean(), "Import ownership is persisted before the network request");
            authentication.Switch("beta");
            check((await Call("workspace")).GetProperty("result").GetProperty("hosts").GetArrayLength() == 0, "Another account starts with an independent empty workspace");
            var betaDirectory = (await Call("workspaceSync")).GetProperty("result").GetProperty("dataDirectory").GetString()!;
            check(alphaDirectory != betaDirectory, "Account caches use different directories");
            check(!(await Call("workspaceSync")).GetProperty("result").GetProperty("legacyImportAvailable").GetBoolean(), "Another account cannot claim an interrupted legacy import");
            authentication.Switch("alpha");
            synchronization.FailImport = false;
            var imported = await Call("importLegacyWorkspace");
            check(imported.GetProperty("ok").GetBoolean(), "The original account can retry its interrupted import");
            var importedWorkspace = imported.GetProperty("result").Deserialize<Workspace>(WorkspaceStore.JsonOptions)!;
            check(importedWorkspace.Hosts.Single().PrivateKeyPath == keyPath && importedWorkspace.KnownHosts.Count == 1, "Import retains private key paths and trust locally");
            check(synchronization.Imported!.Hosts.Single().PrivateKeyPath == "" && synchronization.Imported.KnownHosts.Count == 0, "Import never uploads key paths or host trust");
            check(File.ReadAllText(Path.Combine(directory, "workspace.pre-postgres.json")) == original && File.ReadAllText(Path.Combine(directory, "workspace.json")) == original, "Import preserves both the original workspace and its backup");
            check(!(await Call("workspaceSync")).GetProperty("result").GetProperty("legacyImportAvailable").GetBoolean(), "Completed imports are not offered again");
            authentication.Switch("beta");
            var beta = (await Call("workspace")).GetProperty("result").Deserialize<Workspace>(WorkspaceStore.JsonOptions)!;
            check(beta.Hosts.Count == 0 && beta.KnownHosts.Count == 0, "Switching accounts does not copy hosts or trust records");
            check(!(await Call("favorite", new { id = legacy.Hosts[0].Id })).GetProperty("ok").GetBoolean(), "A foreign account host cannot be changed");
            var saved = await Call("saveHost", new { name = "Beta key host", address = "beta.example.test", group = "Beta", authType = "key", privateKeyPath = keyPath });
            check(saved.GetProperty("ok").GetBoolean() && saved.GetProperty("result").GetProperty("hosts")[0].GetProperty("privateKeyPath").GetString() == keyPath, "Saving a key host retains its device path locally");
            check(!synchronization.LastChange!.Payload.TryGetProperty("privateKeyPath", out _), "Host mutations strip the private key path before transport");
            var previous = synchronization.LastChange;
            check(!(await Call("saveHost", new { name = "Invalid", address = "invalid.test", PrivateKeyPath = keyPath })).GetProperty("ok").GetBoolean() && synchronization.LastChange == previous,
                "Unexpected key-path casing is rejected before transport");
            authentication.Switch("alpha");
            var restored = (await Call("workspace")).GetProperty("result").Deserialize<Workspace>(WorkspaceStore.JsonOptions)!;
            check(restored.Hosts.Single().Name == "Legacy host" && restored.KnownHosts.Count == 1, "Returning to an account restores only its own data and trust");
        }
        finally { await controller.ShutdownAsync(); }

        synchronization.Offline = true;
        host = new Host();
        controller = new WorkspaceController(directory, host, new Dispatcher(), authentication, synchronization);
        try
        {
            var cached = await Call("workspace");
            check(cached.GetProperty("ok").GetBoolean() && cached.GetProperty("result").GetProperty("hosts").GetArrayLength() == 1, "A validated account can read its cache when the API is unavailable");
            check(!(await Call("workspaceSync")).GetProperty("result").GetProperty("connected").GetBoolean(), "Cached mode is reported to the UI");
            check((await Call("saveGroup", new { name = "Saved while offline" })).GetProperty("ok").GetBoolean(), "Validated cached mode persists offline mutations");
            check((await Call("workspaceSync")).GetProperty("result").GetProperty("pending").GetInt32() == 1, "Offline changes remain in a durable outbox");
            authentication.Switch("gamma");
            check(!(await Call("workspace")).GetProperty("ok").GetBoolean(), "An account without its own cache cannot use another account's cache");
        }
        finally { await controller.ShutdownAsync(); }
        await StaleResponseAsync(Path.Combine(directory, "stale"), check, unauthorized: false);
        await StaleResponseAsync(Path.Combine(directory, "stale-unauthorized"), check, unauthorized: true);
    }

    private static async Task StaleResponseAsync(string directory, Action<bool, string> check, bool unauthorized)
    {
        using var authentication = new Authentication();
        var host = new Host();
        var synchronization = new Synchronization(authentication) { PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var controller = new WorkspaceController(directory, host, new Dispatcher(), authentication, synchronization);
        try
        {
            var pending = Send(controller, host, "workspace");
            authentication.Switch("beta");
            if (unauthorized) synchronization.PendingRead.SetException(new WorkspaceAccessException());
            else synchronization.PendingRead.SetResult(new WorkspaceSnapshot(1, new Workspace()));
            var reply = await pending;
            check(!reply.GetProperty("ok").GetBoolean(), "A response from an earlier account is discarded");
            check(authentication.State.IsAuthenticated && authentication.State.User!.Subject == "beta", "A stale API response cannot sign out the new account");
            check(!Directory.EnumerateFiles(directory, "workspace.json", SearchOption.AllDirectories).Any(), "Stale responses never write account cache data");
        }
        finally { await controller.ShutdownAsync(); }
    }

    private static async Task<JsonElement> Send(WorkspaceController controller, Host host, string operation, object? payload = null)
    {
        var id = Guid.NewGuid().ToString("N");
        await controller.HandleAsync(JsonSerializer.SerializeToElement(new { id, op = operation, payload = payload ?? new { } })).WaitAsync(TimeSpan.FromSeconds(5));
        return host.Messages.Single(message => message.GetProperty("type").GetString() == "reply" && message.GetProperty("id").GetString() == id);
    }

    private sealed class Authentication : IWorkspaceAuthentication
    {
        public AccountState State { get; private set; } = new(true, false, "https://iam.example.test", "termlume-desktop", "http://127.0.0.1:47831/callback/", new("alpha", "Alpha", "alpha", ""));
        public long Generation { get; private set; } = 1;
        public void Switch(string subject) { State = State with { IsAuthenticated = true, User = new(subject, subject, subject, "") }; Generation++; }
        public Task<AccountState> ValidateAsync(bool force = false) => Task.FromResult(State);
        public Task<AccountState> LoginAsync(Func<string, Task> openBrowser) => Task.FromResult(State);
        public Task<bool> LogoutAsync() { State = State with { IsAuthenticated = false, User = null }; Generation++; return Task.FromResult(true); }
        public void CancelLogin() { }
        public void Dispose() { }
    }

    private sealed class Synchronization(Authentication authentication) : IWorkspaceSynchronization
    {
        private readonly Dictionary<string, WorkspaceSnapshot> data = [];
        public bool Enabled => true;
        public string Scope => "https://workspace.example.test";
        public bool Offline { get; set; }
        public bool FailImport { get; set; }
        public Workspace? Imported { get; private set; }
        public WorkspaceChange? LastChange { get; private set; }
        public TaskCompletionSource<WorkspaceSnapshot>? PendingRead { get; set; }
        private string Subject => authentication.State.User!.Subject;
        private WorkspaceSnapshot Current => data.GetValueOrDefault(Subject, new WorkspaceSnapshot(0, new Workspace()));
        public Task<WorkspaceSnapshot> ReadAsync(CancellationToken token)
        {
            if (PendingRead is not null) return PendingRead.Task;
            if (Offline) throw new IOException("Synthetic API unavailable");
            return Task.FromResult(new WorkspaceSnapshot(Current.Revision, WorkspaceChanges.Clone(Current.Workspace)));
        }
        public Task<WorkspaceSnapshot> ChangeAsync(WorkspaceChange change, CancellationToken token)
        {
            LastChange = change;
            if (Offline) throw new IOException("Synthetic API unavailable");
            if (change.ExpectedRevision != Current.Revision) throw new WorkspaceConflictException();
            var updated = new WorkspaceSnapshot(Current.Revision + 1, WorkspaceChanges.Apply(Current.Workspace, change.Operation, change.Payload, OperatingSystem.IsMacOS(), change.NewId));
            data[Subject] = updated;
            return Task.FromResult(updated);
        }
        public Task<WorkspaceSnapshot> ImportAsync(WorkspaceImport import, CancellationToken token)
        {
            Imported = WorkspaceChanges.Clone(import.Workspace);
            if (FailImport) throw new IOException("Synthetic import response lost");
            WorkspaceChanges.Validate(import.Workspace, OperatingSystem.IsMacOS());
            var updated = new WorkspaceSnapshot(Current.Revision + 1, WorkspaceChanges.Clone(import.Workspace));
            data[Subject] = updated;
            return Task.FromResult(updated);
        }
        public void Dispose() { }
    }

    private sealed class Host : IWorkspaceHost
    {
        public List<JsonElement> Messages { get; } = [];
        public void Send(object message) => Messages.Add(JsonSerializer.SerializeToElement(message, WorkspaceStore.JsonOptions));
        public Task<T?> RequestAsync<T>(string operation, object? payload = null) => throw new InvalidOperationException("Unexpected native prompt: " + operation);
    }
    private sealed class Dispatcher : IWorkspaceDispatcher
    {
        public void BeginInvoke(Action action) => action();
        public void Invoke(Action action) => action();
        public T Invoke<T>(Func<T> action) => action();
    }
}
