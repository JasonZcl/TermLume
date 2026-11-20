using System.Text.Json;
using TermLume.Shared.Abstractions;
using TermLume.Shared.Application;
using TermLume.Shared.Models;
using TermLume.Shared.Services;

internal static class CollaborationTests
{
    public static async Task RunAsync(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, "synthetic-key"); File.WriteAllText(keyPath, "synthetic key path fixture");
        var auth = new Authentication(); var api = new Api(); var host = new Host();
        var controller = new WorkspaceController(directory, host, new Dispatcher(), auth, api);
        Task<JsonElement> Call(string op, object? payload = null) => Send(controller, host, op, payload);
        async Task<JsonElement> State() => (await Call("workspaceSync")).GetProperty("result");
        try
        {
            await Call("workspace");
            api.Offline = true; await Call("workspace");
            var saved = await Call("saveHost", new { name = "Queued key host", address = "queue.test", group = "Test", authType = "key", privateKeyPath = keyPath });
            check(saved.GetProperty("ok").GetBoolean(), "Offline host creation saves a stable identity before sending");
            var id = saved.GetProperty("result").GetProperty("hosts")[0].GetProperty("id").GetString()!;
            await Call("saveSnippet", new { name = "Queued command", command = "uptime" });
            await Call("favorite", new { id });
            check((await State()).GetProperty("pending").GetInt32() == 3, "Multiple offline operations retain their original order");
            await controller.ShutdownAsync();
            controller = new WorkspaceController(directory, host, new Dispatcher(), auth, api);
            var reopened = (await Call("workspace")).GetProperty("result").Deserialize<Workspace>(WorkspaceStore.JsonOptions)!;
            check(reopened.Hosts.Single().Id == id && reopened.Hosts.Single().Favorite && reopened.Hosts.Single().PrivateKeyPath == keyPath && reopened.Snippets.Count == 1,
                "Restart reconstructs the pending draft and preserves local key paths");
            api.Offline = false; api.LoseNextResponse = true;
            await Call("workspace");
            check((await State()).GetProperty("pending").GetInt32() == 3 && api.Personal.Workspace.Hosts.Count == 1, "Lost successful response retains its operation for safe retry");
            await Call("workspace");
            check((await State()).GetProperty("pending").GetInt32() == 0 && api.Personal.Workspace.Hosts.Single().Id == id && api.Personal.Workspace.Hosts.Single().Favorite && api.Personal.Workspace.Snippets.Count == 1,
                "Retry after a lost response neither duplicates records nor toggles favorites twice");
            check(api.LastChange is not null && !JsonSerializer.Serialize(api.LastChange).Contains(keyPath, StringComparison.Ordinal), "Private key paths never enter queued HTTP payloads");
            api.Offline = true; await Call("workspace"); await Call("saveGroup", new { name = "Local edit" });
            api.ExternalGroup("Remote edit"); api.Offline = false; await Call("workspace");
            check((await State()).GetProperty("conflict").GetBoolean() && (await State()).GetProperty("pending").GetInt32() == 1, "Concurrent remote edits pause replay without discarding pending data");
            check((await Call("workspace")).GetProperty("result").GetProperty("groups").EnumerateArray().Any(item => item.GetString() == "Local edit"), "A conflicted local draft remains visible and exportable");
            await Call("retryPendingChanges");
            check(api.Personal.Workspace.Groups.Contains("Remote edit") && api.Personal.Workspace.Groups.Contains("Local edit") && !(await State()).GetProperty("conflict").GetBoolean(), "Explicit retry rebases pending operations onto the latest confirmed workspace");
            api.Offline = true; await Call("workspace"); await Call("saveGroup", new { name = "Discard me" });
            api.ExternalGroup("Keep remote"); api.Offline = false; await Call("workspace"); await Call("discardPendingChanges");
            check(!api.Personal.Workspace.Groups.Contains("Discard me") && api.Personal.Workspace.Groups.Contains("Keep remote") && (await State()).GetProperty("pending").GetInt32() == 0,
                "Explicit discard removes only pending changes and retains remote edits");
            var portable = WorkspaceChanges.Portable(api.Personal.Workspace);
            var favoritePayload = JsonSerializer.SerializeToElement(new { id, favorite = true });
            var repeatedFavorite = WorkspaceChanges.Apply(WorkspaceChanges.Apply(portable, "favorite", favoritePayload, false), "favorite", favoritePayload, false);
            check(repeatedFavorite.Hosts.Single().Favorite, "Favorite intent survives rebase without inverting a newer remote state");
            var connectedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            var recent = WorkspaceChanges.Apply(portable, "connected", JsonSerializer.SerializeToElement(new { id, at = connectedAt }), false);
            check(recent.Hosts.Single().LastConnectedAt == connectedAt, "Queued recent connections preserve the time of the actual connection");
            var before = (await State()).GetProperty("dataDirectory").GetString();
            var selected = await Call("selectWorkspace", new { id = api.Team.Context!.Id });
            check(selected.GetProperty("ok").GetBoolean() && (await State()).GetProperty("dataDirectory").GetString() != before && (await State()).GetProperty("context").GetProperty("role").GetString() == "viewer", "Shared workspaces have separate caches and report the effective member role");
            check(!(await Call("saveGroup", new { name = "Forbidden shared edit" })).GetProperty("ok").GetBoolean(), "Native boundary rejects shared asset writes by a viewer");
            check((await Call("favorite", new { id = api.Team.Workspace.Hosts[0].Id })).GetProperty("ok").GetBoolean(), "A viewer can still change personal favorites");
            var cancelled = await Call("adoptTrustReference", new { endpoint = api.Reference.Endpoint });
            check(cancelled.GetProperty("ok").GetBoolean() && !cancelled.GetProperty("result").GetBoolean(), "Cloud fingerprints never become local trust without native confirmation");
            host.AcceptTrust = true;
            await Call("adoptTrustReference", new { endpoint = api.Reference.Endpoint });
            check((await Call("knownHosts")).GetProperty("result").GetArrayLength() == 1, "Confirmed cloud fingerprint is saved to the current device only");
            check(!(await Call("adoptTrustReference", new { endpoint = api.Reference.Endpoint })).GetProperty("ok").GetBoolean(), "Cloud adoption cannot overwrite an existing local trust record");
            var exportTarget = Path.Combine(directory, "outside-export.json");
            host.Export = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var export = Call("exportWorkspace");
            check(!export.IsCompleted, "Export waits for the native file picker");
            await Call("selectWorkspace", new { id = api.Personal.Context!.Id });
            host.Export.SetResult(exportTarget);
            var exportReply = await export;
            check(!exportReply.GetProperty("ok").GetBoolean() && exportReply.GetProperty("errorCode").GetString() == "workspace_changed" && auth.State.IsAuthenticated && !File.Exists(exportTarget), "Switching workspaces invalidates an earlier native file dialog");
            check((await Call("knownHosts")).GetProperty("result").GetArrayLength() == 0, "Personal workspace does not inherit shared workspace trust");
            api.Revoked = true;
            var denied = await Call("workspace");
            check(!denied.GetProperty("ok").GetBoolean() && !auth.State.IsAuthenticated, "Revoked API device access signs the desktop out instead of falling back to cache");
        }
        finally { await controller.ShutdownAsync(); }
        await StalePermissionAsync(Path.Combine(directory, "stale-load-permission"), check, loading: true);
        await StalePermissionAsync(Path.Combine(directory, "stale-refresh-permission"), check, loading: false);
    }

    private static async Task StalePermissionAsync(string directory, Action<bool, string> check, bool loading)
    {
        var auth = new Authentication(); var api = new Api(); var host = new Host();
        var controller = new WorkspaceController(directory, host, new Dispatcher(), auth, api);
        Task<JsonElement> Call(string op, object? payload = null) => Send(controller, host, op, payload);
        async Task<string> AccountDirectory() => (await Call("workspaceSync")).GetProperty("result").GetProperty("dataDirectory").GetString()!;
        try
        {
            await Call("workspace");
            var alphaDirectory = await AccountDirectory();
            auth.Switch("beta");
            await Call("workspace");
            var betaSelection = Path.Combine(await AccountDirectory(), "selected-workspace.json");
            var selection = JsonSerializer.Serialize(api.Team.Context!.Id);
            File.WriteAllText(betaSelection, selection);
            auth.Switch("alpha");
            if (loading) File.WriteAllText(Path.Combine(alphaDirectory, "selected-workspace.json"), selection);
            else
            {
                await Call("workspace");
                await Call("selectWorkspace", new { id = api.Team.Context!.Id });
            }
            api.PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var messageCount = host.Messages.Count;
            var pending = Call("workspace");
            check(!pending.IsCompleted, "Permission race waits for the original account's API response");
            auth.Switch("beta");
            api.PendingRead.SetException(new WorkspacePermissionException());
            var reply = await pending;
            check(!reply.GetProperty("ok").GetBoolean(), "A stale permission response cannot complete an old workspace request");
            check(File.ReadAllText(betaSelection) == selection, (loading ? "Loading" : "Refreshing") + " an old workspace cannot clear the new account's selection");
            check(!host.Messages.Skip(messageCount).Any(item => item.GetProperty("type").GetString() == "workspaceAccessLost"), "A stale permission failure cannot lock the new account's workspace");
        }
        finally { await controller.ShutdownAsync(); }
    }

    private static async Task<JsonElement> Send(WorkspaceController controller, Host host, string operation, object? payload)
    {
        var id = Guid.NewGuid().ToString("N");
        await controller.HandleAsync(JsonSerializer.SerializeToElement(new { id, op = operation, payload = payload ?? new { } })).WaitAsync(TimeSpan.FromSeconds(8));
        return host.Messages.Single(message => message.GetProperty("type").GetString() == "reply" && message.GetProperty("id").GetString() == id);
    }
    private sealed class Authentication : IWorkspaceAuthentication
    {
        public AccountState State { get; private set; } = new(true, false, "https://iam.test", "termlume-desktop", "http://127.0.0.1:47831/callback/", new("alpha", "Alpha", "alpha", ""));
        public long Generation { get; private set; } = 1;
        public void Switch(string subject) { State = State with { User = new(subject, subject, subject, "") }; Generation++; }
        public Task<AccountState> ValidateAsync(bool force = false) => Task.FromResult(State);
        public Task<AccountState> LoginAsync(Func<string, Task> openBrowser) => Task.FromResult(State);
        public Task<bool> LogoutAsync() { Generation++; State = State with { IsAuthenticated = false, User = null }; return Task.FromResult(true); }
        public void CancelLogin() { }
        public void Dispose() { }
    }
    private sealed class Api : IWorkspaceSynchronization, IWorkspaceAdministration
    {
        public bool Enabled => true;
        public string Scope => "https://workspace.test";
        public string? SelectedWorkspaceId { get; set; }
        public bool Offline, Revoked, LoseNextResponse;
        public WorkspaceChange? LastChange;
        public TaskCompletionSource<WorkspaceSnapshot>? PendingRead;
        public WorkspaceSnapshot Personal = new(0, new Workspace(), new(Guid.NewGuid().ToString("N"), "Personal", "personal", "owner", 0));
        public WorkspaceSnapshot Team;
        public KnownHostEntry Reference = new("[shared.test]:22 ssh-ed25519", "shared.test", 22, "ssh-ed25519", "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('='), new());
        private readonly HashSet<string> applied = [];
        public Api()
        {
            var value = new Workspace(); value.Groups.Add("Shared"); value.Hosts.Add(new HostProfile { Id = Guid.NewGuid().ToString("N"), Name = "Shared host", Address = "shared.test", Group = "Shared" });
            Team = new(0, value, new(Guid.NewGuid().ToString("N"), "Team", "shared", "viewer", 0));
        }
        private WorkspaceSnapshot Current => SelectedWorkspaceId is null ? Personal : Team;
        private void Available() { if (Revoked) throw new WorkspaceAccessException(); if (Offline) throw new IOException("Synthetic offline API"); }
        public Task<WorkspaceSnapshot> ReadAsync(CancellationToken token) { Available(); return PendingRead?.Task ?? Task.FromResult(Current with { Workspace = WorkspaceChanges.Clone(Current.Workspace) }); }
        public Task<WorkspaceSnapshot> ChangeAsync(WorkspaceChange change, CancellationToken token)
        {
            Available(); LastChange = change;
            if (change.OperationId is { } id && applied.Contains(id)) return ReadAsync(token);
            if (change.ExpectedRevision != Current.Revision) throw new WorkspaceConflictException();
            var next = Current with { Revision = Current.Revision + 1, Workspace = WorkspaceChanges.Apply(Current.Workspace, change.Operation, change.Payload, OperatingSystem.IsMacOS(), change.NewId), Context = Current.Context! with { Revision = Current.Revision + 1 } };
            if (SelectedWorkspaceId is null) Personal = next; else Team = next;
            if (change.OperationId is { } operation) applied.Add(operation);
            if (LoseNextResponse) { LoseNextResponse = false; throw new IOException("Synthetic response lost after commit"); }
            return Task.FromResult(next);
        }
        public Task<WorkspaceSnapshot> ImportAsync(WorkspaceImport import, CancellationToken token) => throw new NotImplementedException();
        public Task<JsonElement> ExecuteAsync(string action, JsonElement payload, CancellationToken token)
        {
            Available(); object result = action switch
            {
                "workspace.list" => new[] { Personal.Context!, Team.Context! },
                "trust.list" => new[] { new { entry = Reference } },
                _ => new { }
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(result, WorkspaceStore.JsonOptions));
        }
        public void ExternalGroup(string name)
        {
            Personal = Personal with { Revision = Personal.Revision + 1, Workspace = WorkspaceChanges.Apply(Personal.Workspace, "saveGroup", JsonSerializer.SerializeToElement(new { name }), OperatingSystem.IsMacOS()) };
        }
        public void Dispose() { }
    }
    private sealed class Host : IWorkspaceHost
    {
        public List<JsonElement> Messages { get; } = [];
        public bool AcceptTrust;
        public TaskCompletionSource<string>? Export;
        public void Send(object message) => Messages.Add(JsonSerializer.SerializeToElement(message, WorkspaceStore.JsonOptions));
        public async Task<T?> RequestAsync<T>(string operation, object? payload = null)
        {
            object? result = operation switch { "trustHost" => AcceptTrust, "exportWorkspace" => Export is null ? null : await Export.Task, _ => throw new InvalidOperationException("Unexpected native request") };
            return result is null ? default : (T)result;
        }
    }
    private sealed class Dispatcher : IWorkspaceDispatcher
    {
        public void BeginInvoke(Action action) => action();
        public void Invoke(Action action) => action();
        public T Invoke<T>(Func<T> action) => action();
    }
}
