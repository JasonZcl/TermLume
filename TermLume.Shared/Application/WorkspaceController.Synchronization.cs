using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TermLume.Shared.Abstractions;
using TermLume.Shared.Models;
using TermLume.Shared.Services;

namespace TermLume.Shared.Application;

public sealed partial class WorkspaceController
{
    private readonly IWorkspaceSynchronization synchronization;
    private readonly Workspace legacyWorkspace;
    private readonly SemaphoreSlim workspaceGate = new(1, 1);
    private CancellationTokenSource accountCancellation = new();
    private string? loadedAccount;
    private long workspaceRevision;
    private bool synchronizationAvailable;
    private string? synchronizationError;

    private string AccountKey()
    {
        var account = authentication.State;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new[] { synchronization.Scope, account.Issuer, account.ClientId, account.User!.Subject }))));
    }

    private WorkspaceOutbox? outbox;
    private WorkspaceDescriptor? workspaceContext;
    private IWorkspaceAdministration Administration => synchronization as IWorkspaceAdministration ?? throw new InvalidOperationException("工作区服务未提供此功能。");
    private string AccountDirectory => Path.Combine(dataDirectory, "accounts", AccountKey());
    private string SelectionPath => Path.Combine(AccountDirectory, "selected-workspace.json");

    private async Task EnsureWorkspaceAsync(long generation)
    {
        if (!synchronization.Enabled || loadedAccount == AccountKey()) return;
        await workspaceGate.WaitAsync();
        try
        {
            RequireAuthenticationGeneration(generation);
            var key = AccountKey();
            if (loadedAccount == key) return;
            accountCancellation.Cancel();
            accountCancellation = new CancellationTokenSource();
            var admin = synchronization as IWorkspaceAdministration;
            if (admin is not null)
            {
                var selected = File.Exists(SelectionPath) ? JsonSerializer.Deserialize<string?>(File.ReadAllText(SelectionPath)) : null;
                if (selected is not null && !Guid.TryParseExact(selected, "N", out _)) throw new InvalidDataException("工作区选择记录无效。");
                admin.SelectedWorkspaceId = selected;
            }
            var directory = admin?.SelectedWorkspaceId is { } selectedId ? Path.Combine(AccountDirectory, "workspaces", selectedId) : AccountDirectory;
            var scopedStore = new WorkspaceStore(directory);
            var local = scopedStore.Load();
            var nextOutbox = WorkspaceOutbox.Load(directory, new WorkspaceSnapshot(0, WorkspaceChanges.Portable(local)));
            WorkspaceSnapshot? snapshot = null;
            try { snapshot = await synchronization.ReadAsync(accountCancellation.Token); }
            catch (WorkspacePermissionException) when (admin?.SelectedWorkspaceId is not null)
            {
                RequireAuthenticationGeneration(generation);
                // 成员移除后不再展示旧团队缓存；仍保留该目录中的待同步数据供本人导出。
                admin.SelectedWorkspaceId = null;
                Directory.CreateDirectory(AccountDirectory);
                File.WriteAllText(SelectionPath, "null");
                scopedStore = new WorkspaceStore(AccountDirectory);
                local = scopedStore.Load();
                nextOutbox = WorkspaceOutbox.Load(AccountDirectory, new WorkspaceSnapshot(0, WorkspaceChanges.Portable(local)));
                snapshot = await synchronization.ReadAsync(accountCancellation.Token);
                host.Send(new { type = "workspaceAccessLost" });
            }
            catch (IOException error) when (error is not (WorkspaceAccessException or WorkspaceConflictException or WorkspacePermissionException) && File.Exists(Path.Combine(scopedStore.DirectoryPath, "workspace.json")))
            { RequireAuthenticationGeneration(generation); synchronizationError = error.Message; }
            RequireAuthenticationGeneration(generation);
            store = scopedStore;
            outbox = nextOutbox;
            if (snapshot is not null && outbox.Pending.Count == 0) outbox.Confirmed = snapshot;
            workspaceContext = snapshot?.Context ?? outbox.Confirmed.Context;
            workspaceRevision = outbox.Confirmed.Revision;
            workspace = OverlayLocal(outbox.Draft(), local);
            foreach (var item in workspace.Hosts) if (item.AuthType == "key" && outbox.LocalKeys.TryGetValue(item.Id, out var keyPath)) item.PrivateKeyPath = keyPath;
            store.Save(workspace);
            outbox.Save();
            activityLog = new ActivityLogStore(scopedStore.DirectoryPath);
            synchronizationAvailable = snapshot is not null;
            if (synchronizationAvailable) synchronizationError = null;
            loadedAccount = key;
            Localization.Language = workspace.Preferences.Language;
            if (snapshot is not null && outbox.Pending.Count > 0 && !outbox.Conflict) await DrainOutboxAsync(generation);
            Record("workspace.loaded", "已打开当前账号的工作区。", category: "application");
        }
        finally { workspaceGate.Release(); }
    }

    private static Workspace OverlayLocal(Workspace remote, Workspace local)
    {
        var result = WorkspaceChanges.Clone(remote);
        var paths = local.Hosts.ToDictionary(host => host.Id, host => host.PrivateKeyPath, StringComparer.Ordinal);
        foreach (var host in result.Hosts) host.PrivateKeyPath = host.AuthType == "key" ? paths.GetValueOrDefault(host.Id, "") : "";
        result.KnownHosts = local.KnownHosts;
        result.KnownHostDetails = local.KnownHostDetails;
        return result;
    }

    private void CommitSnapshot(WorkspaceSnapshot snapshot, Workspace local, long generation)
    {
        RequireAuthenticationGeneration(generation);
        if (outbox is not null) { outbox.Confirmed = snapshot; outbox.Save(); }
        workspaceContext = snapshot.Context;
        var next = OverlayLocal(snapshot.Workspace, local);
        store.Save(next);
        workspace = next;
        workspaceRevision = snapshot.Revision;
        synchronizationAvailable = true;
        synchronizationError = null;
        Localization.Language = next.Preferences.Language;
        // 登录页启动语言属于当前设备设置，账号业务数据仍存放在各自目录中。
        try { File.WriteAllText(Path.Combine(dataDirectory, "device-preferences.json"), JsonSerializer.Serialize(next.Preferences, WorkspaceStore.JsonOptions)); }
        catch (IOException) { }
        host.Send(new { type = "workspaceSyncChanged", state = SynchronizationState() });
    }

    private void SaveDraft(long generation)
    {
        RequireAuthenticationGeneration(generation);
        var next = OverlayLocal(outbox!.Draft(), workspace);
        foreach (var id in outbox.LocalKeys.Keys.Where(id => next.Hosts.All(item => item.Id != id)).ToArray()) outbox.LocalKeys.Remove(id);
        outbox.Save();
        foreach (var item in next.Hosts) if (item.AuthType == "key" && outbox.LocalKeys.TryGetValue(item.Id, out var keyPath)) item.PrivateKeyPath = keyPath;
        store.Save(next);
        workspace = next;
        workspaceRevision = outbox.Confirmed.Revision;
        workspaceContext = outbox.Confirmed.Context ?? workspaceContext;
        Localization.Language = next.Preferences.Language;
        host.Send(new { type = "workspaceSyncChanged", state = SynchronizationState() });
    }

    // 调用方持有 workspaceGate；每次确认响应后先更新写前日志，再更新展示缓存。
    private async Task DrainOutboxAsync(long generation)
    {
        if (outbox is null || outbox.Conflict) return;
        while (outbox.Pending.Count > 0)
        {
            var pending = outbox.Pending[0] with { ExpectedRevision = outbox.Confirmed.Revision };
            try
            {
                var snapshot = await synchronization.ChangeAsync(pending, accountCancellation.Token);
                RequireAuthenticationGeneration(generation);
                outbox.Confirmed = snapshot;
                outbox.Pending.RemoveAt(0);
                outbox.Save();
                synchronizationAvailable = true;
                synchronizationError = null;
                SaveDraft(generation);
            }
            catch (WorkspaceConflictException error)
            {
                RequireAuthenticationGeneration(generation);
                outbox.Conflict = true; outbox.Save(); synchronizationAvailable = true; synchronizationError = error.Message;
                host.Send(new { type = "workspaceSyncChanged", state = SynchronizationState() });
                return;
            }
            catch (WorkspacePermissionException error)
            {
                RequireAuthenticationGeneration(generation);
                outbox.Conflict = true; outbox.Save(); synchronizationError = error.Message;
                host.Send(new { type = "workspaceSyncChanged", state = SynchronizationState() });
                return;
            }
            catch (InvalidDataException error)
            {
                RequireAuthenticationGeneration(generation);
                outbox.Conflict = true; outbox.Save(); synchronizationError = error.Message;
                host.Send(new { type = "workspaceSyncChanged", state = SynchronizationState() });
                throw;
            }
            catch (IOException error) when (error is not WorkspaceAccessException)
            {
                RequireAuthenticationGeneration(generation);
                synchronizationAvailable = false; synchronizationError = error.Message;
                host.Send(new { type = "workspaceSyncChanged", state = SynchronizationState() });
                return;
            }
        }
    }

    private async Task<Workspace> RefreshWorkspaceAsync(long generation)
    {
        if (!synchronization.Enabled) return workspace;
        await workspaceGate.WaitAsync(accountCancellation.Token);
        try
        {
            RequireAuthenticationGeneration(generation);
            var previousRevision = workspaceRevision;
            var previousContext = workspaceContext;
            try
            {
                if (outbox!.Pending.Count > 0)
                {
                    await DrainOutboxAsync(generation);
                    // 即使冲突暂停重放，也继续检查设备撤销和成员资格。
                    if (outbox.Pending.Count > 0)
                    {
                        var latest = await synchronization.ReadAsync(accountCancellation.Token);
                        RequireAuthenticationGeneration(generation);
                        workspaceContext = latest.Context ?? workspaceContext;
                        synchronizationAvailable = true;
                        host.Send(new { type = "workspaceSyncChanged", state = SynchronizationState() });
                    }
                }
                else CommitSnapshot(await synchronization.ReadAsync(accountCancellation.Token), workspace, generation);
            }
            catch (WorkspacePermissionException)
            {
                RequireAuthenticationGeneration(generation);
                // 只读仍可读取；这里的 403 表示成员资格已失效。
                {
                    await CloseAccountSessionsAsync();
                    RequireAuthenticationGeneration(generation);
                    loadedAccount = null;
                    workspace = new Workspace { Preferences = workspace.Preferences };
                    Administration.SelectedWorkspaceId = null;
                    File.WriteAllText(SelectionPath, "null");
                    contextGeneration++;
                    host.Send(new { type = "workspaceAccessLost" });
                }
                throw;
            }
            catch (IOException error) when (error is not (WorkspaceAccessException or WorkspaceConflictException))
            { RequireAuthenticationGeneration(generation); synchronizationAvailable = false; synchronizationError = error.Message; host.Send(new { type = "workspaceSyncChanged", state = SynchronizationState() }); }
            RequireAuthenticationGeneration(generation);
            if (previousRevision != workspaceRevision || previousContext != workspaceContext) host.Send(new { type = "workspaceChanged", workspace });
            return workspace;
        }
        finally { workspaceGate.Release(); }
    }

    private async Task<Workspace> ChangeWorkspaceAsync(string operation, JsonElement payload, long generation)
    {
        await workspaceGate.WaitAsync(accountCancellation.Token);
        try
        {
            RequireAuthenticationGeneration(generation);
            if (workspaceContext is { CanWrite: false } && operation is not ("favorite" or "connected" or "preferences")) throw new WorkspacePermissionException();
            if (outbox!.Conflict) throw new WorkspaceConflictException();
            var portable = JsonNode.Parse(payload.GetRawText())!.AsObject();
            if (operation == "favorite")
            {
                var target = workspace.Hosts.Find(item => item.Id == portable["id"]?.GetValue<string>()) ?? throw new KeyNotFoundException("主机不存在。");
                portable["favorite"] = !target.Favorite;
            }
            if (operation == "connected") portable["at"] = DateTimeOffset.UtcNow;
            string? privateKeyPath = null;
            if (operation == "saveHost")
            {
                var fields = new HashSet<string>(["id", "name", "address", "port", "username", "group", "color", "authType", "privateKeyPath", "note", "favorite", "lastConnectedAt"], StringComparer.Ordinal);
                if (portable.Any(pair => !fields.Contains(pair.Key))) throw new InvalidDataException("主机配置包含不支持的字段。");
                privateKeyPath = portable["privateKeyPath"]?.GetValue<string>() ?? "";
                if (portable["authType"]?.GetValue<string>() == "key" && !File.Exists(privateKeyPath)) throw new InvalidDataException("请选择有效的私钥文件。");
                portable.Remove("privateKeyPath");
            }
            var newId = operation is "saveHost" or "saveSnippet" && string.IsNullOrEmpty(portable["id"]?.GetValue<string>()) ? Guid.NewGuid().ToString("N") : null;
            var change = new WorkspaceChange(workspaceRevision, operation, JsonSerializer.SerializeToElement(portable), Guid.NewGuid().ToString("N"), newId);
            WorkspaceChanges.Apply(WorkspaceChanges.Portable(workspace), operation, change.Payload, OperatingSystem.IsMacOS(), newId);
            var localId = portable["id"]?.GetValue<string>() ?? newId;
            var oldKey = localId is not null ? outbox.LocalKeys.GetValueOrDefault(localId) : null;
            outbox.Pending.Add(change);
            if (localId is not null && privateKeyPath is not null) outbox.LocalKeys[localId] = privateKeyPath;
            try { outbox.Save(); }
            catch { outbox.Pending.Remove(change); if (localId is not null) { if (oldKey is null) outbox.LocalKeys.Remove(localId); else outbox.LocalKeys[localId] = oldKey; } throw; }
            SaveDraft(generation);
            if (synchronizationAvailable) await DrainOutboxAsync(generation);
            return workspace;
        }
        finally { workspaceGate.Release(); }
    }

    private async Task<Workspace> ResolveOutboxAsync(bool retry, long generation)
    {
        await workspaceGate.WaitAsync(accountCancellation.Token);
        try
        {
            RequireAuthenticationGeneration(generation);
            var latest = await synchronization.ReadAsync(accountCancellation.Token);
            RequireAuthenticationGeneration(generation);
            var previous = outbox!.Confirmed;
            outbox.Confirmed = latest;
            if (retry)
            {
                try { outbox.Draft(); }
                catch { outbox.Confirmed = previous; throw new InvalidDataException("远端记录已删除或更改，无法重放这些操作。请先导出本机草稿，再放弃待同步修改。"); }
            }
            else outbox.Pending.Clear();
            outbox.Conflict = false;
            outbox.Save();
            synchronizationAvailable = true; synchronizationError = null;
            SaveDraft(generation);
            if (retry) await DrainOutboxAsync(generation);
            host.Send(new { type = "workspaceChanged", workspace });
            return workspace;
        }
        finally { workspaceGate.Release(); }
    }

    private bool LegacyImportAvailable
    {
        get
        {
            if (!synchronization.Enabled || loadedAccount is null || workspaceContext?.Kind == "shared" ||
                legacyWorkspace.Hosts.Count + legacyWorkspace.Snippets.Count + legacyWorkspace.Groups.Count == 0) return false;
            var path = Path.Combine(dataDirectory, "workspace-import-owner.json");
            if (!File.Exists(path)) return true;
            using var claim = JsonDocument.Parse(File.ReadAllText(path));
            return claim.RootElement.GetProperty("account").GetString() == loadedAccount &&
                claim.RootElement.TryGetProperty("completed", out var completed) && !completed.GetBoolean();
        }
    }

    private void WriteImportClaim(bool completed)
    {
        var path = Path.Combine(dataDirectory, "workspace-import-owner.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { account = loadedAccount, completed, updatedAt = DateTimeOffset.UtcNow }));
        File.Move(temporary, path, true);
    }

    private object SynchronizationState() => new
    {
        enabled = synchronization.Enabled, connected = synchronizationAvailable, revision = workspaceRevision,
        activitySyncEnabled = synchronization.Enabled && loadedAccount is not null && ActivitySynchronizationEnabled,
        context = workspaceContext, pending = outbox?.Pending.Count ?? 0, conflict = outbox?.Conflict ?? false,
        error = synchronizationError, legacyImportAvailable = LegacyImportAvailable,
        dataDirectory = loadedAccount is null ? null : store.DirectoryPath
    };

    private async Task<Workspace> ImportLegacyWorkspaceAsync(long generation)
    {
        var token = accountCancellation.Token;
        await workspaceGate.WaitAsync(token);
        try
        {
            RequireAuthenticationGeneration(generation);
            if (outbox!.Pending.Count > 0) throw new InvalidOperationException("请先处理待同步修改，再导入旧配置。");
            if (!LegacyImportAvailable) throw new InvalidOperationException("没有可导入的旧版工作区。");
            // 多实例之间也只允许一个账号领取这台设备的旧配置。
            using var lease = new FileStream(Path.Combine(dataDirectory, "workspace-import.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!LegacyImportAvailable) throw new InvalidOperationException("旧版工作区已经导入。");
            var original = Path.Combine(dataDirectory, "workspace.json");
            var backup = Path.Combine(dataDirectory, "workspace.pre-postgres.json");
            if (File.Exists(original) && !File.Exists(backup)) File.Copy(original, backup, false);
            // 在网络请求之前落盘归属，即使服务端成功后响应丢失，也不会被其他账号重复领取。
            WriteImportClaim(completed: false);
            var snapshot = await synchronization.ImportAsync(new WorkspaceImport(workspaceRevision, WorkspaceChanges.Portable(legacyWorkspace)), token);
            RequireAuthenticationGeneration(generation);
            var local = WorkspaceChanges.Clone(workspace);
            foreach (var imported in legacyWorkspace.Hosts)
            {
                var existing = local.Hosts.Find(item => item.Id == imported.Id);
                if (existing is null) local.Hosts.Add(imported);
                else existing.PrivateKeyPath = imported.PrivateKeyPath;
            }
            foreach (var pair in legacyWorkspace.KnownHosts) local.KnownHosts.TryAdd(pair.Key, pair.Value);
            foreach (var pair in legacyWorkspace.KnownHostDetails) local.KnownHostDetails.TryAdd(pair.Key, pair.Value);
            CommitSnapshot(snapshot, local, generation);
            WriteImportClaim(completed: true);
            return workspace;
        }
        finally { workspaceGate.Release(); }
    }

    private async Task RecordRecentConnectionAsync(string id, long generation)
    {
        if (!synchronization.Enabled) return;
        try { await ChangeWorkspaceAsync("connected", JsonSerializer.SerializeToElement(new { id }), generation); }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException)
        {
            synchronizationError = error.Message;
            if (error is WorkspaceAccessException) throw;
            // 最近连接时间同步失败不终止已经通过身份校验的 SSH 连接。
        }
    }
}
