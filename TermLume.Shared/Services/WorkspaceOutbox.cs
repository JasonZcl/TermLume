using System.Text.Json;
using TermLume.Shared.Models;

namespace TermLume.Shared.Services;

// 每个账号、工作区独立落盘。写前日志使应用重启和响应丢失都不会重复执行操作。
public sealed class WorkspaceOutbox
{
    public WorkspaceSnapshot Confirmed { get; set; } = new(0, new Workspace());
    public List<WorkspaceChange> Pending { get; set; } = [];
    public Dictionary<string, string> LocalKeys { get; set; } = [];
    public bool Conflict { get; set; }
    private string path = "";
    public static WorkspaceOutbox Load(string directory, WorkspaceSnapshot fallback)
    {
        var path = Path.Combine(directory, "pending-changes.json");
        var result = File.Exists(path)
            ? JsonSerializer.Deserialize<WorkspaceOutbox>(File.ReadAllText(path), WorkspaceStore.JsonOptions) ?? throw new InvalidDataException("待同步队列损坏，请先备份文件后修复。")
            : new WorkspaceOutbox { Confirmed = fallback };
        if (result.Pending is null || result.LocalKeys is null || result.Confirmed is null || result.Pending.Count > 200 || result.LocalKeys.Count > 1000 || result.Confirmed.Revision < 0)
            throw new InvalidDataException("待同步队列无效，请先备份文件后修复。");
        WorkspaceChanges.Validate(result.Confirmed.Workspace, OperatingSystem.IsMacOS());
        result.path = path;
        return result;
    }
    public Workspace Draft()
    {
        var value = WorkspaceChanges.Clone(Confirmed.Workspace);
        foreach (var change in Pending) value = WorkspaceChanges.Apply(value, change.Operation, change.Payload, OperatingSystem.IsMacOS(), change.NewId);
        return value;
    }
    public void Save()
    {
        if (Pending.Count > 200) throw new InvalidDataException("待同步操作已达 200 条，请先同步或处理冲突。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, WorkspaceStore.JsonOptions);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("待同步队列超过 4 MiB，请先同步。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", bytes);
        File.Move(path + ".tmp", path, true);
    }
}

public static class DeviceIdentity
{
    public static DeviceRegistration Load(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "device-id.json");
        string id;
        if (File.Exists(path))
        {
            id = JsonSerializer.Deserialize<string>(File.ReadAllText(path)) ?? "";
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("本机设备标识损坏，请备份 device-id.json 后重新登记。");
        }
        else
        {
            id = Guid.NewGuid().ToString("N");
            try { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); JsonSerializer.Serialize(file, id); }
            catch (IOException) when (File.Exists(path)) { id = JsonSerializer.Deserialize<string>(File.ReadAllText(path))!; if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("本机设备标识无效。"); }
        }
        return new(id, Environment.MachineName, OperatingSystem.IsMacOS() ? "macos" : "windows",
            typeof(DeviceIdentity).Assembly.GetName().Version!.ToString(3));
    }
}
