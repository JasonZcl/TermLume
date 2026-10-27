using System.Globalization;
using TermLume.Shared.Models;

namespace TermLume.Shared.Services;

public enum HostKeyStatus { Unknown, Trusted, Changed, AlgorithmChanged }

/// <summary>按地址、端口和算法管理服务器信任；指纹变化不会自动替换原有信任。</summary>
public sealed class KnownHostRegistry(Workspace workspace, WorkspaceStore store)
{
    private static string HostPrefix(HostProfile host) => $"[{host.Address.ToLowerInvariant()}]:{host.Port} ";
    public static string Endpoint(HostProfile host, string algorithm) => HostPrefix(host) + algorithm;

    private bool HasHost(HostProfile host) => workspace.KnownHosts.Keys.Any(endpoint => endpoint.StartsWith(HostPrefix(host), StringComparison.Ordinal));

    public HostKeyStatus Verify(HostProfile host, string algorithm, string fingerprint)
    {
        var endpoint = Endpoint(host, algorithm);
        if (!workspace.KnownHosts.TryGetValue(endpoint, out var saved))
            return HasHost(host) ? HostKeyStatus.AlgorithmChanged : HostKeyStatus.Unknown;
        var existing = workspace.KnownHostDetails.GetValueOrDefault(endpoint);
        // 使用新的元数据对象，保证保存失败时仍能恢复未被修改的旧记录。
        var metadata = new KnownHostMetadata
        {
            FirstTrustedAt = existing?.FirstTrustedAt, LastVerifiedAt = existing?.LastVerifiedAt,
            LastMismatchAt = existing?.LastMismatchAt, LastMismatchFingerprint = existing?.LastMismatchFingerprint
        };
        var status = saved == fingerprint ? HostKeyStatus.Trusted : HostKeyStatus.Changed;
        if (status == HostKeyStatus.Trusted)
        {
            metadata.LastVerifiedAt = DateTimeOffset.Now;
            metadata.LastMismatchFingerprint = null;
        }
        else
        {
            metadata.LastMismatchAt = DateTimeOffset.Now;
            metadata.LastMismatchFingerprint = fingerprint;
        }
        Persist(endpoint, saved, metadata);
        return status;
    }

    /// <summary>仅在用户确认首次信任后调用；已有指纹必须先显式移除，不能覆盖。</summary>
    public void Trust(HostProfile host, string algorithm, string fingerprint)
    {
        var endpoint = Endpoint(host, algorithm);
        if (HasHost(host)) throw new InvalidOperationException("此服务器已有信任记录，不能通过新增算法重新信任。请核验后在已知主机中显式更新记录。");
        Persist(endpoint, fingerprint, new KnownHostMetadata { FirstTrustedAt = DateTimeOffset.Now, LastVerifiedAt = DateTimeOffset.Now });
    }

    public bool Forget(string endpoint)
    {
        if (!workspace.KnownHosts.ContainsKey(endpoint)) return false;
        Persist(endpoint, null, null);
        return true;
    }

    private void Persist(string endpoint, string? fingerprint, KnownHostMetadata? metadata)
    {
        var previousFingerprint = workspace.KnownHosts.GetValueOrDefault(endpoint);
        var previousMetadata = workspace.KnownHostDetails.GetValueOrDefault(endpoint);
        if (fingerprint is null) workspace.KnownHosts.Remove(endpoint);
        else workspace.KnownHosts[endpoint] = fingerprint;
        if (metadata is null) workspace.KnownHostDetails.Remove(endpoint);
        else workspace.KnownHostDetails[endpoint] = metadata;
        try { store.Save(workspace); }
        catch
        {
            // 磁盘持久化失败时同步回滚内存，避免界面误以为信任变更已经保存。
            if (previousFingerprint is null) workspace.KnownHosts.Remove(endpoint);
            else workspace.KnownHosts[endpoint] = previousFingerprint;
            if (previousMetadata is null) workspace.KnownHostDetails.Remove(endpoint);
            else workspace.KnownHostDetails[endpoint] = previousMetadata;
            throw;
        }
    }

    public List<KnownHostEntry> Entries() => workspace.KnownHosts.Select(pair =>
    {
        var address = pair.Key;
        int? port = null;
        var algorithm = "未知算法";
        var closing = pair.Key.LastIndexOf("]:", StringComparison.Ordinal);
        var separator = closing >= 0 ? pair.Key.IndexOf(' ', closing) : -1;
        if (pair.Key.StartsWith('[') && closing > 0 && separator > closing && int.TryParse(pair.Key.AsSpan(closing + 2, separator - closing - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 1 and <= 65535)
        {
            address = pair.Key[1..closing];
            port = parsed;
            algorithm = pair.Key[(separator + 1)..];
        }
        return new KnownHostEntry(pair.Key, address, port, algorithm, pair.Value, workspace.KnownHostDetails.GetValueOrDefault(pair.Key) ?? new KnownHostMetadata());
    }).OrderBy(entry => entry.Address, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Port).ThenBy(entry => entry.Algorithm).ToList();
}
