namespace TermLume.Shared.Abstractions;

// 只有可公开的账号状态可通过桌面桥发送；令牌不属于工作空间模型。
public sealed record AccountProfile(string Subject, string Name, string Username, string Email);
public sealed record AccountState(bool IsAuthenticated, bool IsLoggingIn, string Issuer, string ClientId,
    string RedirectUri, AccountProfile? User = null, DateTimeOffset? ExpiresAt = null);

public interface IWorkspaceAuthentication : IDisposable
{
    AccountState State { get; }
    long Generation { get; }
    Task<AccountState> LoginAsync(Func<string, Task> openBrowser);
    void CancelLogin();
    Task<AccountState> ValidateAsync(bool force = false);
    Task<bool> LogoutAsync();
}
