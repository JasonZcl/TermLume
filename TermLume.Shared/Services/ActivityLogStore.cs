using System.IO;
using System.Text.Json;
using TermLume.Shared.Models;

namespace TermLume.Shared.Services;

/// <summary>线程安全的有界事件日志；记录连接与安全事件，而非终端输入输出。</summary>
public sealed class ActivityLogStore
{
    public const int Limit = 2000;
    private readonly string filePath;
    private readonly object gate = new();
    private List<ActivityLogEntry> entries = [];
    private bool blocked;
    private string? error;

    public ActivityLogStore(string? dataDirectory = null)
    {
        filePath = Path.Combine(dataDirectory ?? WorkspaceStore.DataDirectory, "activity-log.json");
        try
        {
            if (!File.Exists(filePath)) return;
            var loaded = JsonSerializer.Deserialize<List<ActivityLogEntry>>(File.ReadAllText(filePath), WorkspaceStore.JsonOptions)
                ?? throw new InvalidDataException();
            if (loaded.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Id) || entry.Message is null || entry.Event is null || entry.Details is null || entry.Level is not ("info" or "warning" or "error") || entry.Category is not ("connection" or "security" or "application")))
                throw new InvalidDataException();
            entries = loaded.OrderByDescending(entry => entry.Timestamp).Take(Limit).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // 原日志无法读取时保留原文件；显式清空重建之前，新事件仅存于内存。
            blocked = true;
            error = "日志文件损坏或无法读取，原文件未覆盖。请先备份 activity-log.json，再清空日志以重建。";
        }
    }

    public object Snapshot()
    {
        lock (gate) return new { entries = entries.ToArray(), error, limit = Limit };
    }

    public List<ActivityLogEntry> Select(IReadOnlyCollection<string> ids)
    {
        if (ids.Count > Limit) throw new InvalidDataException("导出记录数量超出上限。");
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        lock (gate) return entries.Where(entry => selected.Contains(entry.Id)).ToList();
    }

    public void Append(ActivityLogEntry entry)
    {
        lock (gate)
        {
            entries.Insert(0, entry);
            if (entries.Count > Limit) entries.RemoveRange(Limit, entries.Count - Limit);
            if (blocked) return;
            try { Save(entries); error = null; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = "日志暂时无法写入磁盘，本次记录仅保留在内存中。请检查磁盘空间和目录权限。";
            }
        }
    }

    public object Clear()
    {
        lock (gate)
        {
            // 必须先成功落盘，再清除内存状态；写入失败不能伪装成已清空。
            Save([]);
            entries.Clear();
            blocked = false;
            error = null;
            return Snapshot();
        }
    }

    private void Save(List<ActivityLogEntry> value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, WorkspaceStore.JsonOptions));
        File.Move(temporaryPath, filePath, true);
    }
}
