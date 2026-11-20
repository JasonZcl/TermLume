using System.Text.Json;
using TermLume.Shared.Models;

namespace TermLume.Shared.Services;

// 服务端和桌面端共用业务字段校验；设备私钥路径和信任记录不会发送到服务端。
public static class WorkspaceChanges
{
    private static readonly JsonSerializerOptions StrictJson = new(WorkspaceStore.JsonOptions)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static Workspace Clone(Workspace value) => JsonSerializer.Deserialize<Workspace>(JsonSerializer.Serialize(value, WorkspaceStore.JsonOptions), WorkspaceStore.JsonOptions)!;

    public static Workspace Portable(Workspace value)
    {
        var result = Clone(value);
        result.KnownHosts.Clear();
        result.KnownHostDetails.Clear();
        foreach (var host in result.Hosts) host.PrivateKeyPath = "";
        return result;
    }

    public static void Validate(Workspace value, bool macOS)
    {
        if (value is null || value.Groups is null || value.Hosts is null || value.Snippets is null || value.Preferences is null ||
            value.KnownHosts is null || value.KnownHostDetails is null || value.Groups.Count > 200 || value.Hosts.Count > 1000 || value.Snippets.Count > 1000 ||
            value.KnownHosts.Count != 0 || value.KnownHostDetails.Count != 0)
            throw new InvalidDataException("工作区结构无效或超出容量限制。");
        if (value.Groups.Any(group => string.IsNullOrWhiteSpace(group) || group != group.Trim() || group.Length > 80) || value.Groups.Distinct(StringComparer.Ordinal).Count() != value.Groups.Count)
            throw new InvalidDataException("分组名称无效或重复。");
        foreach (var host in value.Hosts)
        {
            if (host is null || !Guid.TryParse(host.Id, out _) || string.IsNullOrWhiteSpace(host.Name) || host.Name.Length > 100 ||
                string.IsNullOrWhiteSpace(host.Address) || host.Address.Length > 255 || host.Address.Any(char.IsWhiteSpace) || host.Address.Contains('/') || host.Address.Contains('\\') ||
                string.IsNullOrWhiteSpace(host.Username) || host.Username.Length > 100 || host.Port is < 1 or > 65535 || host.Note is null || host.Note.Length > 2000 ||
                host.AuthType is not ("password" or "key") || host.PrivateKeyPath != "" || !value.Groups.Contains(host.Group) ||
                host.Color is not ("mint" or "blue" or "purple" or "amber"))
                throw new InvalidDataException("主机配置无效，或包含不应同步的本机路径。");
        }
        foreach (var snippet in value.Snippets)
            if (snippet is null || !Guid.TryParse(snippet.Id, out _) || string.IsNullOrWhiteSpace(snippet.Name) || snippet.Name.Length > 100 || string.IsNullOrEmpty(snippet.Command) || snippet.Command.Length > 20000)
                throw new InvalidDataException("命令片段无效或超过长度限制。");
        if (value.Hosts.Select(host => Guid.Parse(host.Id)).Distinct().Count() != value.Hosts.Count || value.Snippets.Select(snippet => Guid.Parse(snippet.Id)).Distinct().Count() != value.Snippets.Count)
            throw new InvalidDataException("工作区存在重复标识。");
        value.Preferences = PreferenceSettings.Normalize(value.Preferences, strict: true, macOS: macOS);
        if (value.Groups.Any(group => group.Contains('\0')) || value.Hosts.Any(host =>
                new[] { host.Name, host.Address, host.Username, host.Note }.Any(text => text.Contains('\0'))) ||
            value.Snippets.Any(snippet => snippet.Name.Contains('\0') || snippet.Command.Contains('\0')))
            throw new InvalidDataException("工作区文本包含无效字符。");
        // 留出快照封装的余量，使服务器允许保存的数据始终能被桌面端的有界缓冲读取。
        if (JsonSerializer.SerializeToUtf8Bytes(value, WorkspaceStore.JsonOptions).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("工作区数据超过 2 MiB，请精简备注或命令片段。");
    }

    public static Workspace Apply(Workspace original, string operation, JsonElement payload, bool macOS, string? newId = null)
    {
        if (newId is not null && !Guid.TryParseExact(newId, "N", out _)) throw new InvalidDataException("新记录标识无效。");
        if (payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException("操作参数必须为对象。");
        var value = Clone(original);
        string Text(string name) => payload.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()! : throw new InvalidDataException("操作参数缺失或类型无效。");
        string Id() => Text("id");
        HostProfile Host(string id) => value.Hosts.Find(host => host.Id == id) ?? throw new KeyNotFoundException("主机不存在。");
        switch (operation)
        {
            case "saveGroup":
                var group = Text("name").Trim();
                if (group.Length is < 1 or > 80 || value.Groups.Contains(group)) throw new InvalidDataException("分组名称无效或已存在。");
                value.Groups.Add(group);
                break;
            case "saveHost":
                var host = payload.Deserialize<HostProfile>(StrictJson) ?? throw new InvalidDataException("主机信息无效。");
                host.Name = host.Name?.Trim() ?? "";
                host.Address = host.Address?.Trim().Trim('[', ']') ?? "";
                host.Username = host.Username?.Trim() ?? "";
                host.Group = string.IsNullOrWhiteSpace(host.Group) ? "未分组" : host.Group.Trim();
                host.Id = payload.TryGetProperty("id", out var hostId) && !string.IsNullOrWhiteSpace(hostId.GetString()) ? Host(hostId.GetString()!).Id : newId ?? Guid.NewGuid().ToString("N");
                var index = value.Hosts.FindIndex(item => item.Id == host.Id);
                if (index >= 0 && (!payload.TryGetProperty("id", out var suppliedId) || string.IsNullOrWhiteSpace(suppliedId.GetString()))) throw new InvalidDataException("新主机标识已存在。");
                if (index >= 0) { host.LastConnectedAt = value.Hosts[index].LastConnectedAt; value.Hosts[index] = host; }
                else { host.LastConnectedAt = null; value.Hosts.Add(host); }
                if (!value.Groups.Contains(host.Group)) value.Groups.Add(host.Group);
                break;
            case "deleteHost":
                var deleted = Host(Id());
                value.Hosts.Remove(deleted);
                break;
            case "favorite":
                var favorite = Host(Id());
                if (payload.TryGetProperty("favorite", out var desired))
                {
                    if (desired.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("收藏状态无效。");
                    favorite.Favorite = desired.GetBoolean();
                }
                else favorite.Favorite = !favorite.Favorite;
                break;
            case "connected":
                var connectedAt = DateTimeOffset.UtcNow;
                if (payload.TryGetProperty("at", out var timestamp) && (timestamp.ValueKind != JsonValueKind.String || !timestamp.TryGetDateTimeOffset(out connectedAt) ||
                    connectedAt < DateTimeOffset.UtcNow.AddYears(-1) || connectedAt > DateTimeOffset.UtcNow.AddMinutes(10))) throw new InvalidDataException("最近连接时间无效。");
                Host(Id()).LastConnectedAt = connectedAt;
                break;
            case "saveSnippet":
                var snippet = payload.Deserialize<Snippet>(StrictJson) ?? throw new InvalidDataException("命令片段无效。");
                snippet.Name = snippet.Name?.Trim() ?? "";
                if (payload.TryGetProperty("id", out var snippetId) && !string.IsNullOrWhiteSpace(snippetId.GetString()))
                {
                    var previous = value.Snippets.FindIndex(item => item.Id == snippetId.GetString());
                    if (previous < 0) throw new KeyNotFoundException("命令片段不存在。");
                    snippet.Id = value.Snippets[previous].Id; value.Snippets[previous] = snippet;
                }
                else { snippet.Id = newId ?? Guid.NewGuid().ToString("N"); value.Snippets.Add(snippet); }
                break;
            case "deleteSnippet":
                var removed = value.Snippets.Find(item => item.Id == Id()) ?? throw new KeyNotFoundException("命令片段不存在。");
                value.Snippets.Remove(removed);
                break;
            case "preferences":
                value.Preferences = payload.Deserialize<Preferences>(StrictJson) ?? throw new InvalidDataException("偏好设置无效。");
                break;
            default: throw new InvalidDataException("不支持的工作区操作。");
        }
        Validate(value, macOS);
        return value;
    }
}
