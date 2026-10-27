using System.IO;
using System.Text;
using System.Threading.Channels;
using Renci.SshNet;
using Renci.SshNet.Common;
using TermLume.Shared.Models;

namespace TermLume.Shared.Services;

/// <summary>管理单个 SSH PTY、串行输入队列和带渲染确认的输出读取流程。</summary>
public sealed class SshSession : IDisposable
{
    public const int MaximumBufferedOutput = ShellStream.MaximumReadBufferLength;
    private readonly SshClient client;
    private readonly SshAuthentication authentication;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim outputAcknowledged = new(0, 1);
    // 输入和终端尺寸变更共用一个消费者，避免并发访问 ShellStream。
    private readonly Channel<Action<ShellStream>> input = Channel.CreateBounded<Action<ShellStream>>(new BoundedChannelOptions(1024) { SingleReader = true });
    private ShellStream? shell;
    private int disposed;
    private int ended;
    public event Action<string>? Output;
    public event Action? Connected;
    public event Action<string?>? Ended;

    public SshSession(HostProfile host, string secret, Func<HostKeyEventArgs, bool> verifyHostKey)
    {
        authentication = new SshAuthentication(host, secret);
        client = new SshClient(authentication.Connection) { KeepAliveInterval = TimeSpan.FromSeconds(25) };
        client.HostKeyReceived += (_, args) => args.CanTrust = verifyHostKey(args);
        client.ErrorOccurred += (_, args) => End(args.Exception.Message == "Terminal receive buffer limit exceeded."
            ? "远端输出超过安全缓冲上限，连接已断开。" : args.Exception.Message);
    }

    /// <summary>后台启动建连，避免同步握手或清理路径阻塞工作区的主机指纹确认调度。</summary>
    public async Task ConnectAsync(uint columns, uint rows)
    {
        await Task.Run(() => client.ConnectAsync(lifetime.Token), lifetime.Token).ConfigureAwait(false);
        lifetime.Token.ThrowIfCancellationRequested();
        var stream = await Task.Run(() => client.CreateShellStream("xterm-256color", columns, rows, 0, 0, 65536), lifetime.Token).ConfigureAwait(false);
        shell = stream;
        if (lifetime.IsCancellationRequested || Volatile.Read(ref disposed) != 0)
        {
            stream.Dispose();
            throw new OperationCanceledException();
        }
        Connected?.Invoke();
        _ = Task.Run(() => ReadOutput(stream));
        _ = ProcessInputAsync(stream);
    }

    public bool Write(string text)
    {
        if (text.Length > 1_000_000) throw new InvalidDataException("单次粘贴不能超过 1 MB。");
        return input.Writer.TryWrite(stream =>
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        });
    }

    public void Resize(uint columns, uint rows) => input.Writer.TryWrite(stream => stream.ChangeWindowSize(columns, rows, 0, 0));

    /// <summary>由前端完成当前批次终端写入后确认，允许读取线程继续投递输出。</summary>
    public void AcknowledgeOutput()
    {
        if (outputAcknowledged.CurrentCount == 0) outputAcknowledged.Release();
    }

    private async Task ProcessInputAsync(ShellStream stream)
    {
        try
        {
            await foreach (var action in input.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                await Task.Run(() => action(stream), lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SshException or ObjectDisposedException or OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested) End(exception.Message);
        }
    }

    private void ReadOutput(ShellStream stream)
    {
        string? error = null;
        try
        {
            var bytes = new byte[16384];
            var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            // 解码器跨读取批次保留状态，避免 UTF-8 字符被网络分块截断时出现乱码。
            var decoder = Encoding.UTF8.GetDecoder();
            while (!lifetime.IsCancellationRequested)
            {
                var count = stream.Read(bytes, 0, bytes.Length);
                if (count == 0) break;
                var characterCount = decoder.GetChars(bytes, 0, count, characters, 0);
                if (characterCount > 0 && Output is not null)
                {
                    Output.Invoke(new string(characters, 0, characterCount));
                    // 等待 xterm.js 消费当前批次，避免持续输出淹没 WebView2 消息队列。
                    if (!outputAcknowledged.Wait(TimeSpan.FromSeconds(30), lifetime.Token))
                        throw new IOException("终端界面响应超时，为保护内存已断开连接。");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SshException or ObjectDisposedException or OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested) error = exception.Message;
        }
        finally
        {
            if (!lifetime.IsCancellationRequested) End(error);
        }
    }

    private void End(string? error)
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref ended, 1) != 0) return;
        lifetime.Cancel();
        input.Writer.TryComplete();
        try { Ended?.Invoke(error); }
        finally { _ = Task.Run(Dispose); }
    }

    public void Dispose()
    {
        // 读取结束、输入失败及用户断开都可能触发释放，必须保证清理只执行一次。
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        input.Writer.TryComplete();
        shell?.Dispose();
        client.Dispose();
        authentication.Dispose();
    }
}
