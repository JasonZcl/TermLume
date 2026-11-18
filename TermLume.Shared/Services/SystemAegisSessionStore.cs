using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TermLume.Shared.Abstractions;

namespace TermLume.Shared.Services;

public sealed partial class SystemAegisSessionStore : IAegisSessionStore
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly string target;
    private readonly string markerPath;
    private FileStream? lease;
    private bool disposed;

    public SystemAegisSessionStore(AegisOptions options, string dataDirectory)
    {
        directory = Path.GetFullPath(dataDirectory);
        var scope = OperatingSystem.IsWindows() ? directory.ToUpperInvariant() : directory;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { scope, options.Issuer, options.ClientId }))));
        target = "TermLume/Aegis-IAM/" + key;
        markerPath = Path.Combine(directory, "account-session-" + key + ".enabled");
    }

    private void EnsureLease()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (lease is not null) return;
        Directory.CreateDirectory(directory);
        try { lease = new FileStream(markerPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("另一个 TermLume 实例正在使用登录会话，请先关闭该实例。"); }
    }

    public SavedAegisSession? Read()
    {
        lock (gate)
        {
            EnsureLease();
            // 标记文件只包含一个字节。清除标记后，即使凭据库暂时拒绝删除，也不能恢复旧登录。
            if (!File.Exists(markerPath)) { DeleteNative(); return null; }
            var bytes = ReadNative();
            if (bytes is null) { File.Delete(markerPath); return null; }
            try
            {
                if (bytes.Length > 2560) throw new JsonException();
                return JsonSerializer.Deserialize<SavedAegisSession>(bytes) ?? throw new JsonException();
            }
            catch (JsonException) { Delete(); return null; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    public void Write(SavedAegisSession session)
    {
        lock (gate)
        {
            EnsureLease();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(session);
            try
            {
                if (bytes.Length > 2560) throw new IOException("登录会话超出系统凭据存储限制。");
                File.Delete(markerPath);
                WriteNative(bytes);
                File.WriteAllText(markerPath, "1", Encoding.ASCII);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    public void Delete()
    {
        lock (gate)
        {
            EnsureLease();
            try { File.Delete(markerPath); }
            finally { DeleteNative(); }
        }
    }

    private byte[]? ReadNative() => OperatingSystem.IsWindows() ? WindowsRead(target) :
        OperatingSystem.IsMacOS() ? MacRead(target) : throw new PlatformNotSupportedException("当前系统不支持安全保存登录会话。");
    private void WriteNative(byte[] bytes)
    {
        if (OperatingSystem.IsWindows()) WindowsWrite(target, bytes);
        else if (OperatingSystem.IsMacOS()) MacWrite(target, bytes);
        else throw new PlatformNotSupportedException("当前系统不支持安全保存登录会话。");
    }
    private void DeleteNative()
    {
        if (OperatingSystem.IsWindows()) WindowsDelete(target);
        else if (OperatingSystem.IsMacOS()) MacDelete(target);
        else throw new PlatformNotSupportedException("当前系统不支持安全保存登录会话。");
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; lease?.Dispose(); lease = null; }
    }
}
