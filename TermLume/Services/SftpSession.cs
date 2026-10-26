using System.IO;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using TermLume.Models;

namespace TermLume.Services;

public sealed class SftpSession : IDisposable
{
    private readonly SshAuthentication authentication;
    private readonly SftpClient client;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;
    public HostProfile Host { get; }
    public event Action? Ended;

    public SftpSession(HostProfile host, string secret, Func<HostKeyEventArgs, bool> verifyHostKey)
    {
        Host = host;
        authentication = new SshAuthentication(host, secret);
        client = new SftpClient(authentication.Connection)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(25), OperationTimeout = TimeSpan.FromSeconds(30)
        };
        client.HostKeyReceived += (_, args) => args.CanTrust = verifyHostKey(args);
        client.ErrorOccurred += (_, _) => { if (!lifetime.IsCancellationRequested) Ended?.Invoke(); };
    }

    public async Task<FileListing> ConnectAsync()
    {
        await client.ConnectAsync(lifetime.Token).ConfigureAwait(false);
        return await ListAsync(".").ConfigureAwait(false);
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellation = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellation);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (!client.IsConnected) throw new SshConnectionException("SFTP 连接已断开，请重新连接。");
            return await Task.Run(() => operation(linked.Token), linked.Token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public Task<FileListing> ListAsync(string path) => RunAsync(async token =>
    {
        await client.ChangeDirectoryAsync(path, token).ConfigureAwait(false);
        var current = client.WorkingDirectory;
        var entries = new List<FileEntry>();
        await foreach (var item in client.ListDirectoryAsync(current, token).ConfigureAwait(false))
        {
            if (item.Name is "." or "..") continue;
            if (entries.Count >= 10000) throw new InvalidDataException("目录超过 10,000 项，请输入更具体的路径。");
            if (item.Name.Length == 0 || item.Name.Contains('/') || item.Name.Contains('\0')) throw new InvalidDataException("服务器返回了无效的目录项目。");
            entries.Add(new(item.Name, current.TrimEnd('/') + "/" + item.Name, item.IsDirectory, item.IsSymbolicLink, item.Length, item.LastWriteTimeUtc));
        }
        var parent = current == "/" ? null : current[..Math.Max(1, current.LastIndexOf('/'))];
        return new FileListing(current, parent, entries.OrderByDescending(entry => entry.Directory).ThenBy(entry => entry.Name, StringComparer.Ordinal).ToArray());
    });

    private async Task<ISftpFile> GetEntryAsync(string path, CancellationToken token)
    {
        if (!path.StartsWith('/') || path.Split('/').Any(part => part is "." or "..")) throw new InvalidDataException("请使用完整远端路径。");
        var parent = path[..Math.Max(1, path.LastIndexOf('/'))];
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name.Length == 0 || name.Contains('\0')) throw new InvalidDataException("无效的远端路径。");
        var count = 0;
        await foreach (var entry in client.ListDirectoryAsync(parent, token).ConfigureAwait(false))
        {
            if (++count > 10002) throw new InvalidDataException("目录超过 10,000 项，暂不支持此操作。");
            if (entry.Name == name) return entry;
        }
        throw new SftpPathNotFoundException("远端项目已不存在，请刷新目录。");
    }

    public Task<bool> MutateAsync(string action, string path, string? name) => RunAsync(async token =>
    {
        token.ThrowIfCancellationRequested();
        if (action == "mkdir") client.CreateDirectory(Join(path, Name(name)));
        else
        {
            if (path == "/" || !path.StartsWith('/') || path.Split('/').Any(part => part is "." or ".."))
                throw new InvalidDataException("不允许修改此路径。");
            var file = await GetEntryAsync(path, token).ConfigureAwait(false);
            if (action == "rename")
            {
                var target = Join(path[..Math.Max(1, path.LastIndexOf('/'))], Name(name));
                if (client.Exists(target)) throw new IOException("目标已存在，请使用其他名称。");
                file.MoveTo(target);
            }
            else if (action == "delete")
            {
                if (file.IsSymbolicLink || file.IsDirectory || file.IsRegularFile) file.Delete();
                else throw new InvalidDataException("暂不支持此文件类型。");
            }
            else throw new InvalidDataException("未知文件操作。");
        }
        return true;
    });

    public Task<bool> TransferAsync(bool upload, string localPath, string remotePath, Action<long, long> progress, CancellationToken cancellation, Action? validateLocal = null) => RunAsync(async token =>
    {
        validateLocal?.Invoke();
        var remoteTemporary = Join(remotePath[..Math.Max(1, remotePath.LastIndexOf('/'))], ".termlume-" + Guid.NewGuid().ToString("N") + ".part");
        var localTemporary = Path.Combine(Path.GetDirectoryName(localPath)!, ".termlume-" + Guid.NewGuid().ToString("N") + ".part");
        var temporaryCreated = false;
        try
        {
            if (upload)
            {
                if (client.Exists(remotePath)) throw new IOException("远端已存在同名文件，请重命名后重试；未覆盖任何文件。");
                await using var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using (var target = client.Open(remoteTemporary, FileMode.CreateNew, FileAccess.Write))
                {
                    temporaryCreated = true;
                    await CopyAsync(source, target, source.Length, progress, token).ConfigureAwait(false);
                    await target.FlushAsync(token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                if (client.Exists(remotePath)) throw new IOException("目标文件在传输期间被创建，已停止提交。");
                var temporary = await GetEntryAsync(remoteTemporary, token).ConfigureAwait(false);
                temporary.MoveTo(remotePath);
            }
            else
            {
                if (File.Exists(localPath) || Directory.Exists(localPath)) throw new IOException("本地已存在同名文件，请更换文件夹或重命名；未覆盖任何文件。");
                var file = await GetEntryAsync(remotePath, token).ConfigureAwait(false);
                if (!file.IsRegularFile || file.IsSymbolicLink) throw new InvalidDataException("仅支持普通文件传输，不跟随符号链接。");
                await using (var target = new FileStream(localTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    temporaryCreated = true;
                    await using var source = client.OpenRead(remotePath);
                    await CopyAsync(source, target, file.Length, progress, token).ConfigureAwait(false);
                    await target.FlushAsync(token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                File.Move(localTemporary, localPath, false);
            }
            temporaryCreated = false;
            return true;
        }
        finally
        {
            if (temporaryCreated)
            {
                try
                {
                    if (upload) (await GetEntryAsync(remoteTemporary, CancellationToken.None).ConfigureAwait(false)).Delete();
                    else File.Delete(localTemporary);
                }
                catch (Exception exception) when (exception is IOException or SshException or ObjectDisposedException) { }
            }
        }
    }, cancellation);

    private static async Task CopyAsync(Stream source, Stream target, long total, Action<long, long> progress, CancellationToken token)
    {
        var buffer = new byte[65536];
        long transferred = 0;
        progress(0, total);
        while (true)
        {
            var count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            transferred += count;
            progress(transferred, total);
        }
    }

    public static string Join(string directory, string name) => directory.TrimEnd('/') + "/" + Name(name);
    public static string Name(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 240 || name.Contains('/') || name.Contains('\\') || name.Any(char.IsControl))
            throw new InvalidDataException("名称不能为空，不能包含路径分隔符或控制字符，长度不能超过 240。");
        return name;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        _ = Task.Run(async () =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { client.Dispose(); authentication.Dispose(); }
            finally { gate.Release(); }
        });
    }
}
