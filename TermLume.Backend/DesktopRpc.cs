using System.Collections.Concurrent;
using System.Text.Json;
using TermLume.Services;

namespace TermLume.Backend;

internal sealed class DesktopRpc
{
    private readonly object outputLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private static readonly JsonSerializerOptions Options = new(WorkspaceStore.JsonOptions) { WriteIndented = false };

    public void Send(object message)
    {
        var line = JsonSerializer.Serialize(message, Options);
        if (line.Length >= 16 * 1024 * 1024) throw new InvalidDataException("返回数据过大，请选择较小的目录或缩小筛选范围。");
        lock (outputLock) Console.Out.WriteLine(line);
    }

    public async Task<T?> RequestAsync<T>(string operation, object? payload = null)
    {
        if (pending.Count >= 32) throw new InvalidOperationException("原生对话框请求过多，请稍后重试。");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) throw new InvalidOperationException();
        try
        {
            Send(new { type = "hostRequest", id, operation, payload });
            var reply = await completion.Task.WaitAsync(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            return reply.ValueKind is JsonValueKind.Null ? default : reply.Deserialize<T>(WorkspaceStore.JsonOptions);
        }
        finally { pending.TryRemove(id, out _); }
    }

    public void Complete(JsonElement reply)
    {
        if (!reply.TryGetProperty("id", out var id) || !pending.TryRemove(id.GetString() ?? "", out var completion)) return;
        if (reply.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            completion.TrySetResult(reply.GetProperty("result").Clone());
        else completion.TrySetException(new IOException("原生操作失败或已取消。"));
    }

    public void Cancel()
    {
        foreach (var pair in pending)
            if (pending.TryRemove(pair.Key, out var completion)) completion.TrySetCanceled();
    }
}
