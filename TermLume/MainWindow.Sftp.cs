using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using TermLume.Services;
using TermLume.Views;

namespace TermLume;

public partial class MainWindow
{
    private readonly Dictionary<string, SftpSession> sftpSessions = [];
    private readonly Dictionary<string, TransferJob> transferJobs = [];
    private LocalFileBrowser? localFiles;

    private sealed class TransferJob
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public required string SessionId { get; init; }
        public required string Name { get; init; }
        public required string Direction { get; init; }
        public string Status { get; set; } = "queued";
        public long Bytes { get; set; }
        public long Total { get; set; }
        public string? Error { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public CancellationTokenSource Cancellation { get; } = new();
    }

    private async Task<object?> HandleSftpAsync(string operation, JsonElement payload)
    {
        if (operation == "sftpConnect") return await ConnectSftpAsync(payload);
        if (operation == "sftpDefaultLocal")
        {
            var previousBrowser = localFiles;
            var browser = LocalFileBrowser.ForComputer();
            var listing = await Task.Run(() => browser.List(browser.Root));
            if (ReferenceEquals(localFiles, previousBrowser)) localFiles = browser;
            return listing;
        }
        if (operation == "sftpPickLocal")
        {
            var picker = new OpenFolderDialog { Title = Localization.Text("选择 SFTP 本地文件夹"), Multiselect = false };
            if (picker.ShowDialog(this) != true) return null;
            var browser = new LocalFileBrowser(picker.FolderName);
            var listing = await Task.Run(() => browser.List(browser.Root));
            localFiles = browser;
            return listing;
        }
        if (operation == "sftpLocalList")
        {
            var browser = localFiles ?? throw new InvalidOperationException("请先选择本地文件夹。");
            var path = payload.GetProperty("path").GetString()!;
            return await Task.Run(() => browser.List(path));
        }
        if (operation == "sftpCancel")
        {
            if (transferJobs.TryGetValue(payload.GetProperty("id").GetString()!, out var job)) job.Cancellation.Cancel();
            return true;
        }
        var sessionId = payload.GetProperty("sessionId").GetString()!;
        if (operation == "sftpDisconnect") return DisconnectSftp(sessionId);
        if (!sftpSessions.TryGetValue(sessionId, out var session)) throw new InvalidOperationException("SFTP 会话已断开，请重新连接。");
        var remotePath = payload.GetProperty("path").GetString() ?? "";
        if (operation == "sftpList") return await session.ListAsync(remotePath);
        if (operation == "sftpTransfer") return StartTransfer(sessionId, session, payload);
        if (operation == "sftpMutate")
        {
            if (transferJobs.Values.Any(job => job.SessionId == sessionId)) throw new InvalidOperationException("请等待当前传输结束或取消传输后再修改远端文件。");
            var action = payload.GetProperty("action").GetString()!;
            var name = payload.TryGetProperty("name", out var value) ? value.GetString() : null;
            await session.MutateAsync(action, remotePath, name);
            Record("sftp.fileChanged", action switch { "mkdir" => "已创建 SFTP 远端文件夹。", "rename" => "已重命名 SFTP 远端项目。", _ => "已删除 SFTP 远端项目。" }, session.Host, sessionId, details: new() { ["operation"] = action });
            return true;
        }
        throw new InvalidOperationException("未知 SFTP 操作。");
    }

    private async Task<object> ConnectSftpAsync(JsonElement payload)
    {
        if (sftpSessions.Count >= 4) throw new InvalidOperationException("最多同时打开 4 个 SFTP 连接。");
        var host = GetHost(payload.GetProperty("hostId").GetString()!);
        var id = Guid.NewGuid().ToString("N");
        var dialog = new CredentialDialog(this, Localization.Text("连接 SFTP · ") + host.Name,
            $"{host.Username}@{host.Address}:{host.Port}\n" + (host.AuthType == "key" ? "请输入私钥口令；未加密私钥可留空。" : "请输入 SSH 密码。"), host.AuthType == "key");
        if (dialog.ShowDialog() != true)
        {
            Record("sftp.cancelled", "用户取消 SFTP 认证。", host, id, level: "warning");
            throw new OperationCanceledException("已取消 SFTP 连接。");
        }
        Record("sftp.connecting", "开始建立 SFTP 连接。", host, id);
        SftpSession? session = null;
        try
        {
            session = new SftpSession(host, dialog.TakeSecret(), key => VerifyHostKey(host, key, id));
            session.Ended += () => Dispatcher.BeginInvoke(() =>
            {
                if (shuttingDown || !sftpSessions.ContainsKey(id)) return;
                DisconnectSftp(id);
                Record("sftp.connectionLost", "SFTP 连接意外断开，未完成的传输已取消。", host, id, level: "error");
                Post(new { type = "sftpEnded", sessionId = id, error = "SFTP 连接已断开，请重新连接。" });
            });
            sftpSessions.Add(id, session);
            var listing = await session.ConnectAsync();
            if (shuttingDown || !sftpSessions.ContainsKey(id)) throw new OperationCanceledException();
            Record("sftp.connected", "SFTP 连接成功。", host, id);
            return new { sessionId = id, hostId = host.Id, listing };
        }
        catch
        {
            sftpSessions.Remove(id);
            session?.Dispose();
            Record("sftp.failed", "SFTP 连接失败或已取消，请检查凭据、服务器 SFTP 支持及网络。", host, id, level: "error");
            throw;
        }
    }

    private object StartTransfer(string sessionId, SftpSession session, JsonElement payload)
    {
        if (transferJobs.Count >= 100) throw new InvalidOperationException("传输队列最多 100 个文件，请等待任务结束。");
        var browser = localFiles ?? throw new InvalidOperationException("请先选择本地文件夹。");
        var upload = payload.GetProperty("direction").GetString() switch
        {
            "upload" => true, "download" => false, _ => throw new InvalidDataException("无效的传输方向。")
        };
        var remotePath = payload.GetProperty("path").GetString()!;
        var localPath = payload.GetProperty("localPath").GetString()!;
        string name;
        if (upload)
        {
            localPath = browser.Validate(localPath);
            var file = new FileInfo(localPath);
            if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("仅支持上传普通文件。");
            name = file.Name;
            remotePath = SftpSession.Join(remotePath, name);
        }
        else
        {
            name = SftpSession.Name(remotePath[(remotePath.LastIndexOf('/') + 1)..]);
            localPath = browser.Destination(localPath, name);
        }
        return QueueTransfer(sessionId, session, browser, upload, localPath, remotePath, name);
    }

    private async Task<object> StartDroppedUploadsAsync(JsonElement payload, IReadOnlyList<object> additionalObjects)
    {
        var sessionId = payload.GetProperty("sessionId").GetString()!;
        if (!sftpSessions.TryGetValue(sessionId, out var session)) throw new InvalidOperationException("SFTP 会话已断开，请重新连接。");
        if (additionalObjects.Count == 0 || additionalObjects.Count > 100 - transferJobs.Count)
            throw new InvalidOperationException("请选择文件；传输队列最多容纳 100 个活动任务。");
        var directory = payload.GetProperty("path").GetString()!;
        var paths = additionalObjects.Select(item =>
        {
            if (item is not Microsoft.Web.WebView2.Core.CoreWebView2File file)
                throw new InvalidDataException("只能拖入资源管理器中的普通文件。");
            return file.Path;
        }).ToArray();
        var files = await Task.Run(() => paths.Select(source =>
        {
            var browser = LocalFileBrowser.ForDroppedFile(source);
            var path = browser.Validate(source);
            var name = Path.GetFileName(path);
            return (Browser: browser, Path: path, Name: name, Remote: SftpSession.Join(directory, name));
        }).ToArray());
        if (files.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() != files.Length)
            throw new InvalidDataException("拖入的文件包含重复名称，请分开上传。");
        if (shuttingDown || !sftpSessions.TryGetValue(sessionId, out var current) || !ReferenceEquals(session, current))
            throw new OperationCanceledException("SFTP 会话已断开，请重新连接。");
        if (files.Length > 100 - transferJobs.Count) throw new InvalidOperationException("传输队列最多 100 个文件，请等待任务结束。");
        return files.Select(file => QueueTransfer(sessionId, session, file.Browser, true, file.Path, file.Remote, file.Name)).ToArray();
    }

    private TransferJob QueueTransfer(string sessionId, SftpSession session, LocalFileBrowser browser, bool upload, string localPath, string remotePath, string name)
    {
        var job = new TransferJob { SessionId = sessionId, Name = name, Direction = upload ? "upload" : "download" };
        transferJobs.Add(job.Id, job);
        _ = RunTransferAsync(job, session, browser, upload, localPath, remotePath);
        return job;
    }

    private async Task RunTransferAsync(TransferJob job, SftpSession session, LocalFileBrowser browser, bool upload, string localPath, string remotePath)
    {
        var timer = Stopwatch.StartNew();
        var lastUpdate = -200L;
        long latestBytes = 0, latestTotal = 0;
        try
        {
            await session.TransferAsync(upload, localPath, remotePath, (bytes, total) =>
            {
                Interlocked.Exchange(ref latestBytes, bytes);
                Interlocked.Exchange(ref latestTotal, total);
                var now = timer.ElapsedMilliseconds;
                if (now - lastUpdate < 150 && bytes != total) return;
                lastUpdate = now;
                Dispatcher.BeginInvoke(() =>
                {
                    if (job.Status is "completed" or "cancelled" or "failed") return;
                    job.Status = "running"; job.Bytes = bytes; job.Total = total;
                    Post(new { type = "sftpProgress", job });
                });
            }, job.Cancellation.Token, () => browser.Validate(upload ? localPath : Path.GetDirectoryName(localPath)!));
            job.Status = "completed";
            job.Bytes = Interlocked.Read(ref latestBytes);
            job.Total = Interlocked.Read(ref latestTotal);
            Record("sftp.transferred", upload ? "SFTP 文件上传完成。" : "SFTP 文件下载完成。", session.Host, job.SessionId,
                details: new() { ["direction"] = job.Direction, ["bytes"] = job.Bytes.ToString() });
        }
        catch (Exception exception)
        {
            job.Status = exception is OperationCanceledException || job.Cancellation.IsCancellationRequested ? "cancelled" : "failed";
            job.Error = job.Status == "cancelled" ? "传输已取消。" : exception.Message;
            Record("sftp." + job.Status, job.Status == "cancelled" ? "SFTP 文件传输已取消。" : "SFTP 文件传输失败。", session.Host, job.SessionId,
                level: job.Status == "failed" ? "error" : "warning", details: new() { ["direction"] = job.Direction });
        }
        finally
        {
            transferJobs.Remove(job.Id);
            Post(new { type = "sftpProgress", job });
            job.Cancellation.Dispose();
        }
    }

    private bool DisconnectSftp(string sessionId)
    {
        foreach (var job in transferJobs.Values.Where(job => job.SessionId == sessionId)) job.Cancellation.Cancel();
        if (sftpSessions.Remove(sessionId, out var session))
        {
            session.Dispose();
            Record("sftp.disconnected", "SFTP 连接已关闭。", session.Host, sessionId);
        }
        return true;
    }
}
