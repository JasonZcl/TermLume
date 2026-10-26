using System.Globalization;
using TermLume.Models;

namespace TermLume.Services;

public enum HostKeyStatus { Unknown, Trusted, Changed }

public sealed class KnownHostRegistry(Workspace workspace, WorkspaceStore store)
{
    public static string Endpoint(HostProfile host, string algorithm) => $"[{host.Address.ToLowerInvariant()}]:{host.Port} {algorithm}";

    public HostKeyStatus Verify(HostProfile host, string algorithm, string fingerprint)
    {
        var endpoint = Endpoint(host, algorithm);
        if (!workspace.KnownHosts.TryGetValue(endpoint, out var saved)) return HostKeyStatus.Unknown;
        var existing = workspace.KnownHostDetails.GetValueOrDefault(endpoint);
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

    public void Trust(HostProfile host, string algorithm, string fingerprint)
    {
        var endpoint = Endpoint(host, algorithm);
        if (workspace.KnownHosts.ContainsKey(endpoint)) throw new InvalidOperationException("已存在信任记录，不能覆盖原有指纹。");
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
