using System.IO;
using System.Text.Json;
using TermLume.Models;

namespace TermLume.Services;

/// <summary>读写本地工作空间，并拒绝用默认配置覆盖损坏或不完整的已有文件。</summary>
public sealed class WorkspaceStore
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    // 用户数据与程序目录、版本分离，升级和重新安装时继续读取同一份配置。
    public static string DataDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "TermLume")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermLume");
    private readonly string directory;
    private readonly string filePath;

    // 测试可注入隔离目录，避免访问真实用户工作空间。
    public WorkspaceStore(string? dataDirectory = null)
    {
        directory = dataDirectory ?? DataDirectory;
        filePath = Path.Combine(directory, "workspace.json");
    }

    public Workspace Load()
    {
        Directory.CreateDirectory(directory);
        if (!File.Exists(filePath)) return new Workspace { Preferences = PreferenceSettings.Normalize(new Preferences()) };
        var workspace = JsonSerializer.Deserialize<Workspace>(File.ReadAllText(filePath), JsonOptions)
            ?? throw new InvalidDataException("工作空间文件为空。请先备份并检查 workspace.json。");
        if (workspace.Groups is null || workspace.Hosts is null || workspace.Snippets is null || workspace.KnownHosts is null || workspace.KnownHostDetails is null || workspace.Preferences is null)
            throw new InvalidDataException("工作空间文件结构不完整。请先备份并检查 workspace.json。");
        if (workspace.Hosts.Any(host => host is null || string.IsNullOrWhiteSpace(host.Id) || host.Name is null || host.Address is null || host.Username is null || host.Group is null || host.Note is null || host.PrivateKeyPath is null || host.Port is < 1 or > 65535 || host.AuthType is not ("password" or "key")) ||
            workspace.Snippets.Any(snippet => snippet is null || string.IsNullOrWhiteSpace(snippet.Id) || snippet.Name is null || snippet.Command is null) ||
            workspace.Hosts.Select(host => host.Id).Distinct().Count() != workspace.Hosts.Count ||
            workspace.Snippets.Select(snippet => snippet.Id).Distinct().Count() != workspace.Snippets.Count)
            throw new InvalidDataException("工作空间含有无效记录。请先备份并检查 workspace.json。");
        if (workspace.KnownHosts.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) || workspace.KnownHostDetails.Any(pair => pair.Value is null))
            throw new InvalidDataException("工作空间含有无效信任记录。请先备份并检查 workspace.json。");
        if (workspace.Groups.Any(group => string.IsNullOrWhiteSpace(group) || group.Trim().Length > 80))
            throw new InvalidDataException("工作空间含有无效分组。请先备份并检查 workspace.json。");
        foreach (var host in workspace.Hosts)
            host.Group = string.IsNullOrWhiteSpace(host.Group) ? "未分组" : host.Group.Trim();
        MergeGroups(workspace);
        workspace.Preferences = PreferenceSettings.Normalize(workspace.Preferences);
        return workspace;
    }

    public void Save(Workspace workspace)
    {
        MergeGroups(workspace);
        Directory.CreateDirectory(directory);
        // 先在同一目录写入临时文件，再替换正式文件，减少配置被写到一半的风险。
        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(workspace, JsonOptions));
        File.Move(temporaryPath, filePath, true);
    }

    private static void MergeGroups(Workspace workspace)
    {
        // 保留用户创建的空分组，同时兼容只有主机分组字段的旧版配置。
        workspace.Groups = workspace.Groups.Select(group => group.Trim())
            .Concat(workspace.Hosts.Select(host => host.Group))
            .Distinct(StringComparer.Ordinal).ToList();
    }
}
