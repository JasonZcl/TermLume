namespace TermLume.Shared.Models;

/// <summary>持久化的工作空间；连接会话、传输队列和认证凭据不属于保存内容。</summary>
public sealed class Workspace
{
    public List<string> Groups { get; set; } = [];
    public List<HostProfile> Hosts { get; set; } = [];
    public List<Snippet> Snippets { get; set; } = [];
    public Dictionary<string, string> KnownHosts { get; set; } = [];
    public Dictionary<string, KnownHostMetadata> KnownHostDetails { get; set; } = [];
    public Preferences Preferences { get; set; } = new();
}

public sealed class KnownHostMetadata
{
    public DateTimeOffset? FirstTrustedAt { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset? LastMismatchAt { get; set; }
    public string? LastMismatchFingerprint { get; set; }
}

public sealed record KnownHostEntry(string Endpoint, string Address, int? Port, string Algorithm, string Fingerprint, KnownHostMetadata Metadata);

public sealed class ActivityLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Level { get; set; } = "info";
    public string Category { get; set; } = "connection";
    public string Event { get; set; } = "";
    public string Message { get; set; } = "";
    public string? HostId { get; set; }
    public string? HostName { get; set; }
    public string? Address { get; set; }
    public int? Port { get; set; }
    public string? Username { get; set; }
    public string? SessionId { get; set; }
    public long? DurationSeconds { get; set; }
    public Dictionary<string, string> Details { get; set; } = [];
}

/// <summary>保存连接参数及私钥路径，不保存登录密码、私钥内容或解锁口令。</summary>
public sealed class HostProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "root";
    public string Group { get; set; } = "未分组";
    public string Color { get; set; } = "mint";
    public string AuthType { get; set; } = "password";
    public string PrivateKeyPath { get; set; } = "";
    public string Note { get; set; } = "";
    public bool Favorite { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
}

public sealed class Snippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
}

/// <summary>前后端共享的偏好结构；加载与保存前由 PreferenceSettings 统一规范化。</summary>
public sealed class Preferences
{
    public string Language { get; set; } = "zh-CN";
    public int FontSize { get; set; } = 14;
    public string TerminalTheme { get; set; } = "midnight";
    public string FontFamily { get; set; } = "cascadia";
    public double LineHeight { get; set; } = 1.2;
    public int Scrollback { get; set; } = 10000;
    public string CursorStyle { get; set; } = "block";
    public bool CursorBlink { get; set; } = true;
    public string Accent { get; set; } = "lavender";
    public string Density { get; set; } = "compact";
    public bool SidebarCollapsed { get; set; }
    public Dictionary<string, string> Shortcuts { get; set; } = [];
}
