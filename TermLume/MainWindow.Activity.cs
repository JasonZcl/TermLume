using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using TermLume.Models;
using TermLume.Services;

namespace TermLume;

public partial class MainWindow
{
    private readonly ActivityLogStore activityLog = new();
    private readonly Dictionary<string, SessionActivity> sessionActivity = [];
    private KnownHostRegistry KnownHosts => new(workspace, store);

    private sealed class SessionActivity(HostProfile host)
    {
        public HostProfile Host { get; } = host;
        public DateTimeOffset? ConnectedAt { get; set; }
        public bool Closed { get; set; }
        public long? Duration => ConnectedAt is { } connected ? Math.Max(0, (long)(DateTimeOffset.UtcNow - connected).TotalSeconds) : null;
    }

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
        if (!sessionActivity.Remove(sessionId, out var activity)) return;
        activity.Closed = true;
        Record(eventName, message, activity.Host, sessionId, level, duration: activity.Duration);
    }

    private void NotifyKnownHosts() => Post(new { type = "knownHostsChanged", records = KnownHosts.Entries() });

    private bool ExportLogs(JsonElement payload)
    {
        var ids = payload.GetProperty("ids").Deserialize<List<string>>(WorkspaceStore.JsonOptions) ?? [];
        var entries = activityLog.Select(ids);
        if (entries.Count == 0) throw new InvalidDataException("没有可导出的日志，请刷新列表后重试。");
        var dialog = new SaveFileDialog
        {
            Title = Localization.Text("导出筛选后的日志"), Filter = Localization.Text("JSON 日志|*.json"), DefaultExt = ".json", AddExtension = true,
            FileName = $"TermLume-logs-{DateTime.Now:yyyyMMdd-HHmmss}.json", OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return false;
        var target = Path.GetFullPath(dialog.FileName);
        if (new[] { "workspace.json", "activity-log.json" }.Any(name => string.Equals(target, Path.Combine(WorkspaceStore.DataDirectory, name), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("请勿覆盖应用配置或活动日志文件，请选择其他导出位置。");
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { application = "TermLume", exportedAt = DateTimeOffset.Now, entries }, WorkspaceStore.JsonOptions));
        return true;
    }
}
