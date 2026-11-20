using System.Text.Json;
using TermLume.Shared.Models;
using TermLume.Shared.Services;

namespace TermLume.Shared.Application;

public sealed partial class WorkspaceController
{
    private async Task<object> AdministerWorkspaceAsync(JsonElement payload, long generation)
    {
        var action = payload.GetProperty("action").GetString() ?? "";
        var arguments = payload.TryGetProperty("arguments", out var value) ? value : JsonSerializer.SerializeToElement(new { });
        var allowed = new HashSet<string>(["capabilities", "workspace.list", "workspace.create", "workspace.rename", "workspace.delete", "workspace.invite", "workspace.invites", "workspace.revokeInvite", "workspace.join", "workspace.members", "workspace.memberRole", "workspace.removeMember", "workspace.leave", "workspace.transfer", "workspace.history", "workspace.historyItem", "workspace.restore", "device.list", "device.rename", "device.revoke", "trust.list", "trust.publish", "trust.remove", "activity.list", "activity.clear", "audit.list", "activity.settings"], StringComparer.Ordinal);
        if (!allowed.Contains(action)) throw new InvalidDataException("不支持的工作区管理操作。");
        await workspaceGate.WaitAsync(accountCancellation.Token);
        try
        {
            RequireAuthenticationGeneration(generation);
            if (action == "activity.settings")
            {
                var enabled = arguments.GetProperty("enabled").GetBoolean();
                File.WriteAllText(Path.Combine(store.DirectoryPath, "activity-sync-enabled.json"), JsonSerializer.Serialize(enabled));
                return new { enabled };
            }
            if (outbox!.Pending.Count > 0 && action is "workspace.restore" or "workspace.delete" or "workspace.leave" or "workspace.transfer")
                throw new InvalidOperationException("请先处理待同步修改，再执行此操作。");
            if (action == "trust.publish")
            {
                var endpoint = arguments.GetProperty("endpoint").GetString();
                var local = KnownHosts.Entries().Find(item => item.Endpoint == endpoint) ?? throw new InvalidDataException("本机尚未信任此服务器。");
                arguments = JsonSerializer.SerializeToElement(new { entry = local }, WorkspaceStore.JsonOptions);
            }
            var result = await Administration.ExecuteAsync(action, arguments, accountCancellation.Token);
            RequireAuthenticationGeneration(generation);
            if (action == "workspace.restore")
            {
                var snapshot = result.Deserialize<WorkspaceSnapshot>(WorkspaceStore.JsonOptions)!;
                WorkspaceChanges.Validate(snapshot.Workspace, OperatingSystem.IsMacOS());
                CommitSnapshot(snapshot, workspace, generation);
                host.Send(new { type = "workspaceChanged", workspace });
            }
            if (action == "activity.clear") MarkActivityAcknowledged(activityLog.Entries());
            if (action == "device.revoke" && arguments.GetProperty("id").GetString() == DeviceIdentity.Load(dataDirectory).Id)
                throw new WorkspaceAccessException();
            return result;
        }
        finally { workspaceGate.Release(); }
    }

    private async Task<Workspace> SelectWorkspaceAsync(JsonElement payload, long generation)
    {
        var id = payload.GetProperty("id").GetString();
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("工作区标识无效。");
        long selectedGeneration;
        await workspaceGate.WaitAsync(accountCancellation.Token);
        try
        {
            var list = await Administration.ExecuteAsync("workspace.list", JsonSerializer.SerializeToElement(new { }), accountCancellation.Token);
            RequireAuthenticationGeneration(generation);
            var descriptor = list.Deserialize<List<WorkspaceDescriptor>>(WorkspaceStore.JsonOptions)!.Find(item => item.Id == id) ?? throw new WorkspacePermissionException();
            contextGeneration++;
            var nextGeneration = CurrentGeneration;
            selectedGeneration = nextGeneration;
            accountCancellation.Cancel();
            await CloseAccountSessionsAsync();
            RequireAuthenticationGeneration(nextGeneration);
            Directory.CreateDirectory(AccountDirectory);
            File.WriteAllText(SelectionPath, JsonSerializer.Serialize(descriptor.Kind == "personal" ? null : id));
            loadedAccount = null;
            workspace = new Workspace { Preferences = workspace.Preferences };
            outbox = null;
            workspaceContext = null;
        }
        finally { workspaceGate.Release(); }
        await EnsureWorkspaceAsync(selectedGeneration);
        RequireAuthenticationGeneration(selectedGeneration);
        host.Send(new { type = "workspaceChanged", workspace });
        host.Send(new { type = "workspaceSyncChanged", state = SynchronizationState() });
        return workspace;
    }

    private bool ActivitySynchronizationEnabled
    {
        get
        {
            var path = Path.Combine(store.DirectoryPath, "activity-sync-enabled.json");
            return !File.Exists(path) || JsonSerializer.Deserialize<bool>(File.ReadAllText(path));
        }
    }
    private HashSet<string> ActivityAcknowledged()
    {
        var path = Path.Combine(store.DirectoryPath, "activity-synced.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? [] : [];
    }
    private void MarkActivityAcknowledged(IEnumerable<ActivityLogEntry> entries)
    {
        var all = ActivityAcknowledged();
        all.UnionWith(entries.Select(item => item.Id));
        all.IntersectWith(activityLog.Entries().Select(item => item.Id));
        var path = Path.Combine(store.DirectoryPath, "activity-synced.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(all));
        File.Move(path + ".tmp", path, true);
    }
    private async Task SynchronizeActivityAsync(long generation)
    {
        if (synchronization is not Abstractions.IWorkspaceAdministration) return;
        await workspaceGate.WaitAsync(accountCancellation.Token);
        try
        {
            RequireAuthenticationGeneration(generation);
            if (!synchronizationAvailable || !ActivitySynchronizationEnabled) return;
            var acknowledged = ActivityAcknowledged();
            var batch = new List<ActivityLogEntry>();
            foreach (var entry in activityLog.Entries().Where(item => !acknowledged.Contains(item.Id)).Take(200))
            {
                try { batch.Add(ActivityPrivacy.Sanitize(entry)); }
                catch (InvalidDataException) { MarkActivityAcknowledged([entry]); }
            }
            if (batch.Count == 0) return;
            await Administration.ExecuteAsync("activity.upload", JsonSerializer.SerializeToElement(new { entries = batch }, WorkspaceStore.JsonOptions), accountCancellation.Token);
            RequireAuthenticationGeneration(generation);
            MarkActivityAcknowledged(batch);
        }
        finally { workspaceGate.Release(); }
    }

    private async Task<bool> AdoptTrustReferenceAsync(JsonElement payload, long generation)
    {
        var endpoint = payload.GetProperty("endpoint").GetString();
        var references = await Administration.ExecuteAsync("trust.list", JsonSerializer.SerializeToElement(new { }), accountCancellation.Token);
        RequireAuthenticationGeneration(generation);
        var entry = references.EnumerateArray().Select(item => item.GetProperty("entry").Deserialize<KnownHostEntry>(WorkspaceStore.JsonOptions)!).FirstOrDefault(item => item.Endpoint == endpoint)
            ?? throw new InvalidDataException("此指纹参考记录已移除。");
        TrustReferenceValidation.Validate(entry);
        var accepted = await host.RequestAsync<bool>("trustHost", new
        {
            title = "核验并采用服务器指纹",
            message = $"请通过可信渠道核验下列指纹，再保存到本机。\n\n{entry.Address}:{entry.Port}\n算法：{entry.Algorithm}\n指纹：{entry.Fingerprint}\n\n账号中的参考记录不会自动成为本机信任；已有信任不能直接覆盖。"
        });
        RequireAuthenticationGeneration(generation);
        if (!accepted) return false;
        KnownHosts.Trust(new HostProfile { Address = entry.Address, Port = entry.Port!.Value }, entry.Algorithm, entry.Fingerprint);
        Record("hostkey.adopted", "已核验并采用工作区指纹参考。", category: "security", server: entry);
        NotifyKnownHosts();
        return true;
    }

    private async Task<bool> ExportWorkspaceAsync(long generation)
    {
        var snapshot = WorkspaceChanges.Portable(workspace);
        var selected = await host.RequestAsync<string>("exportWorkspace", new { title = "导出工作区草稿", name = $"TermLume-workspace-{DateTime.Now:yyyyMMdd-HHmmss}.json" });
        RequireAuthenticationGeneration(generation);
        if (selected is null) return false;
        var target = Path.GetFullPath(selected);
        if (target.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择应用配置目录以外的导出位置。");
        File.WriteAllText(target, JsonSerializer.Serialize(new { application = "TermLume", schemaVersion = 1, exportedAt = DateTimeOffset.Now, workspace = snapshot }, WorkspaceStore.JsonOptions));
        return true;
    }
    private async Task<Workspace> ImportWorkspaceFileAsync(long generation)
    {
        var selected = await host.RequestAsync<string>("importWorkspace", new { title = "导入 TermLume 工作区" });
        RequireAuthenticationGeneration(generation);
        if (selected is null) return workspace;
        if (new FileInfo(selected).Length > 3 * 1024 * 1024) throw new InvalidDataException("工作区文件超过大小限制。");
        using var file = JsonDocument.Parse(File.ReadAllText(selected));
        var root = file.RootElement;
        if (root.GetProperty("application").GetString() != "TermLume" || root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("不是受支持的 TermLume 工作区文件。");
        var imported = root.GetProperty("workspace").Deserialize<Workspace>(WorkspaceStore.JsonOptions) ?? throw new InvalidDataException("工作区文件无效。");
        WorkspaceChanges.Validate(imported, OperatingSystem.IsMacOS());
        await workspaceGate.WaitAsync(accountCancellation.Token);
        try
        {
            RequireAuthenticationGeneration(generation);
            if (outbox!.Pending.Count > 0) throw new InvalidOperationException("请先处理待同步修改，再导入文件。");
            var snapshot = await synchronization.ImportAsync(new WorkspaceImport(workspaceRevision, imported), accountCancellation.Token);
            CommitSnapshot(snapshot, workspace, generation);
            host.Send(new { type = "workspaceChanged", workspace });
            return workspace;
        }
        finally { workspaceGate.Release(); }
    }
}
