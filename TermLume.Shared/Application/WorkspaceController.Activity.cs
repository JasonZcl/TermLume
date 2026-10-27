using System.IO;
using System.Text.Json;

using TermLume.Shared.Models;
using TermLume.Shared.Services;

namespace TermLume.Shared.Application;

public sealed partial class WorkspaceController
{
    private readonly ActivityLogStore activityLog;
    private readonly Dictionary<string, SessionActivity> sessionActivity = [];
    private KnownHostRegistry KnownHosts => new(workspace, store);

    private sealed class SessionActivity(HostProfile host)
    {
        public HostProfile Host { get; } = host;
        public DateTimeOffset? ConnectedAt { get; set; }
        public bool Closed { get; set; }
        public long? Duration => ConnectedAt is { } connected ? Math.Max(0, (long)(DateTimeOffset.UtcNow - connected).TotalSeconds) : null;
    }

    // 调用方只传递事件元数据，不传密码、终端内容、私钥内容或完整传输路径。
    private void Record(string eventName, string message, HostProfile? host = null, string? sessionId = null, string level = "info", string category = "connection", long? duration = null, Dictionary<string, string>? details = null, KnownHostEntry? server = null)
    {
        activityLog.Append(new ActivityLogEntry
        {
            Event = eventName, Message = message, Level = level, Category = category,
            HostId = host?.Id, HostName = host?.Name, Address = host?.Address ?? server?.Address, Port = host?.Port ?? server?.Port,
            Username = host?.Username, SessionId = sessionId, DurationSeconds = duration, Details = details ?? []
        });
        Post(new { type = "activityChanged" });
    }

    private void EndSessionActivity(string sessionId, string eventName, string message, string level = "info")
    {
        // 主动断开和远端断开可能先后到达，同一会话只记录一次结束事件。
        if (!sessionActivity.Remove(sessionId, out var activity)) return;
        activity.Closed = true;
        Record(eventName, message, activity.Host, sessionId, level, duration: activity.Duration);
    }

    private void NotifyKnownHosts() => Post(new { type = "knownHostsChanged", records = KnownHosts.Entries() });

    private async Task<bool> ExportLogsAsync(JsonElement payload)
    {
        var ids = payload.GetProperty("ids").Deserialize<List<string>>(WorkspaceStore.JsonOptions) ?? [];
        var entries = activityLog.Select(ids);
        if (entries.Count == 0) throw new InvalidDataException("没有可导出的日志，请刷新列表后重试。");
        var selected = await host.RequestAsync<string>("exportLogs", new { title = Localization.Text("导出筛选后的日志"), name = $"TermLume-logs-{DateTime.Now:yyyyMMdd-HHmmss}.json" });
        if (selected is null) return false;
        var target = Path.GetFullPath(selected);
        // 导出是独立副本，不能通过保存对话框覆盖应用正在使用的配置或日志。
        if (new[] { "workspace.json", "activity-log.json" }.Any(name => string.Equals(target, Path.Combine(dataDirectory, name), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("请勿覆盖应用配置或活动日志文件，请选择其他导出位置。");
        File.WriteAllText(target, JsonSerializer.Serialize(new { application = "TermLume", exportedAt = DateTimeOffset.Now, entries }, WorkspaceStore.JsonOptions));
        return true;
    }
}
