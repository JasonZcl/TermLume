using System.IO;
using System.Text.Json;
using TermLume.Models;

namespace TermLume.Services;

public sealed class WorkspaceStore
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermLume");
    private readonly string directory;
    private readonly string filePath;

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
        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(workspace, JsonOptions));
        File.Move(temporaryPath, filePath, true);
    }

    private static void MergeGroups(Workspace workspace)
    {
        workspace.Groups = workspace.Groups.Select(group => group.Trim())
            .Concat(workspace.Hosts.Select(host => host.Group))
            .Distinct(StringComparer.Ordinal).ToList();
    }
}
