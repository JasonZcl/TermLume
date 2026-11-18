using TermLume.Shared.Abstractions;

// 现有工作区 / SSH 测试只替换认证依赖；生产入口始终使用 AegisAuthentication。
internal sealed class TestAccount : IWorkspaceAuthentication
{
    public AccountState State { get; private set; } = new(true, false, "https://example.test", "test-client",
        "http://127.0.0.1:47831/callback/", new("synthetic", "Synthetic", "test", ""), DateTimeOffset.UtcNow.AddHours(1));
    public long Generation { get; private set; }
    public Task<AccountState> LoginAsync(Func<string, Task> openBrowser) => Task.FromResult(State);
    public void CancelLogin() { }
    public Task<AccountState> ValidateAsync(bool force = false) => Task.FromResult(State);
    public Task<bool> LogoutAsync() { State = State with { IsAuthenticated = false, User = null }; Generation++; return Task.FromResult(true); }
    public void Dispose() { }
}
