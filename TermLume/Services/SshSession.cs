using System.IO;
using System.Text;
using System.Threading.Channels;
using Renci.SshNet;
using Renci.SshNet.Common;
using TermLume.Models;

namespace TermLume.Services;

public sealed class SshSession : IDisposable
{
    private readonly SshClient client;
    private readonly SshAuthentication authentication;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim outputAcknowledged = new(0, 1);
    private readonly Channel<Action<ShellStream>> input = Channel.CreateBounded<Action<ShellStream>>(new BoundedChannelOptions(1024) { SingleReader = true });
    private ShellStream? shell;
    private int disposed;
    public event Action<string>? Output;
    public event Action? Connected;
    public event Action<string?>? Ended;

    public SshSession(HostProfile host, string secret, Func<HostKeyEventArgs, bool> verifyHostKey)
    {
        authentication = new SshAuthentication(host, secret);
        client = new SshClient(authentication.Connection) { KeepAliveInterval = TimeSpan.FromSeconds(25) };
        client.HostKeyReceived += (_, args) => args.CanTrust = verifyHostKey(args);
    }

    public async Task ConnectAsync(uint columns, uint rows)
    {
        await client.ConnectAsync(lifetime.Token).ConfigureAwait(false);
        lifetime.Token.ThrowIfCancellationRequested();
        var stream = await Task.Run(() => client.CreateShellStream("xterm-256color", columns, rows, 0, 0, 65536), lifetime.Token).ConfigureAwait(false);
        shell = stream;
        if (Volatile.Read(ref disposed) != 0)
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
            if (!lifetime.IsCancellationRequested) Ended?.Invoke(exception.Message);
            Dispose();
        }
    }

    private void ReadOutput(ShellStream stream)
    {
        string? error = null;
        try
        {
            var bytes = new byte[16384];
            var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var decoder = Encoding.UTF8.GetDecoder();
            while (!lifetime.IsCancellationRequested)
            {
                var count = stream.Read(bytes, 0, bytes.Length);
                if (count == 0) break;
                var characterCount = decoder.GetChars(bytes, 0, count, characters, 0);
                if (characterCount > 0 && Output is not null)
                {
                    Output.Invoke(new string(characters, 0, characterCount));
                    outputAcknowledged.Wait(lifetime.Token);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SshException or ObjectDisposedException or OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested) error = exception.Message;
        }
        finally
        {
            if (!lifetime.IsCancellationRequested) Ended?.Invoke(error);
            Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        input.Writer.TryComplete();
        shell?.Dispose();
        client.Dispose();
        authentication.Dispose();
    }
}
