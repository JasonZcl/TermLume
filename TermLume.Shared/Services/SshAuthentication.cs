using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;
using TermLume.Shared.Models;

namespace TermLume.Shared.Services;

/// <summary>供 SSH 与 SFTP 共用的一次性认证资源；连接结束后由会话统一释放。</summary>
public sealed class SshAuthentication : IDisposable
{
    private readonly PrivateKeyFile? key;
    private readonly AuthenticationMethod method;
    public ConnectionInfo Connection { get; }

    public SshAuthentication(HostProfile host, string secret)
    {
        if (host.AuthType == "key")
        {
            key = string.IsNullOrEmpty(secret) ? new PrivateKeyFile(host.PrivateKeyPath) : new PrivateKeyFile(host.PrivateKeyPath, secret);
            method = new PrivateKeyAuthenticationMethod(host.Username, new ModernPrivateKeySource(key));
        }
        else method = new PasswordAuthenticationMethod(host.Username, secret);
        Connection = new ConnectionInfo(host.Address, host.Port, host.Username, method) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public void Dispose()
    {
        method.Dispose();
        key?.Dispose();
    }

    private sealed class ModernPrivateKeySource(PrivateKeyFile key) : IPrivateKeySource
    {
        // 排除旧的 RSA/SHA-1 与 DSA 签名算法，不影响同一 RSA 私钥使用 SHA-2 认证。
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; } = key.HostKeyAlgorithms
            .Where(algorithm => algorithm.Name is not ("ssh-rsa" or "ssh-dss")).ToArray();
    }
}
