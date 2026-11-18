using TermLume.Shared.Abstractions;

namespace TermLume.Shared.Application;

public sealed partial class WorkspaceController
{
    private sealed class AccountRequiredException() : InvalidOperationException("请先登录 Aegis-IAM。");

    private AccountState AuthenticationState => signingOut
        ? authentication.State with { IsAuthenticated = false, IsLoggingIn = false, User = null, ExpiresAt = null }
        : authentication.State;

    private void RequireAuthenticationGeneration(long generation)
    {
        if (shuttingDown || signingOut || !authentication.State.IsAuthenticated || generation != authentication.Generation)
            throw new AccountRequiredException();
    }

    private async Task<long> RequireAuthenticationAsync()
    {
        var generation = authentication.Generation;
        if (signingOut || !(await RefreshAuthenticationAsync()).IsAuthenticated) throw new AccountRequiredException();
        // 异步校验返回的快照可能早于退出或切换账号，执行操作前必须重新核验当前状态。
        RequireAuthenticationGeneration(generation);
        return generation;
    }

    private async Task<AccountState> RefreshAuthenticationAsync()
    {
        var authenticated = authentication.State.IsAuthenticated;
        var state = await authentication.ValidateAsync();
        if (authenticated && !state.IsAuthenticated)
        {
            // 校验失败先锁界面，再释放会话；不再向登录页面交付工作区内容。
            NotifyAuthentication();
            await CloseAccountSessionsAsync();
        }
        return AuthenticationState;
    }

    private async void CheckAuthentication()
    {
        if (shuttingDown || signingOut || checkingAuthentication || !authentication.State.IsAuthenticated) return;
        checkingAuthentication = true;
        try { await RefreshAuthenticationAsync(); }
        catch (OperationCanceledException) when (shuttingDown) { }
        finally { checkingAuthentication = false; }
    }

    private async Task<AccountState> LoginAsync()
    {
        if (signingOut) throw new InvalidOperationException("正在退出登录，请稍后重试。");
        try
        {
            return await authentication.LoginAsync(async url =>
            {
                var opened = await host.RequestAsync<bool>("openBrowser", new { url });
                if (!opened) throw new IOException("无法打开系统浏览器，请检查默认浏览器设置。");
            });
        }
        finally { NotifyAuthentication(); }
    }

    private bool CancelLogin() { authentication.CancelLogin(); return true; }

    private async Task<object> LogoutAsync()
    {
        if (signingOut) throw new InvalidOperationException("正在退出登录，请稍后重试。");
        signingOut = true;
        try
        {
            NotifyAuthentication();
            authentication.CancelLogin();
            var logout = authentication.LogoutAsync();
            // 等待网络撤销时也立即阻止所有工作区操作。
            await CloseAccountSessionsAsync();
            var revocationSucceeded = await logout;
            NotifyAuthentication();
            return new { authentication = authentication.State, revocationSucceeded };
        }
        finally { signingOut = false; }
    }

    private async Task CloseAccountSessionsAsync()
    {
        foreach (var job in transferJobs.Values) job.Cancellation.Cancel();
        var ssh = sessions.Values.ToArray();
        var sftp = sftpSessions.Values.ToArray();
        foreach (var id in sessions.Keys.ToArray()) EndSessionActivity(id, "connection.disconnected", "账号已退出或授权失效，SSH 连接已关闭。");
        sessions.Clear();
        sftpSessions.Clear();
        localFiles = null;
        await Task.Run(() =>
        {
            foreach (var session in ssh) session.Dispose();
            foreach (var session in sftp) session.Dispose();
        });
    }

    private void NotifyAuthentication()
    {
        if (!shuttingDown) host.Send(new { type = "authenticationChanged", authentication = AuthenticationState });
    }
}
