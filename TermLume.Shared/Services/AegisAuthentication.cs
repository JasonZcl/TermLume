using System.Security.Cryptography;
using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using TermLume.Shared.Abstractions;

namespace TermLume.Shared.Services;

public sealed class AegisAuthentication : IWorkspaceAuthentication
{
    private readonly AegisOptions configuration;
    private readonly IAegisSessionStore sessionStore;
    private readonly SemaphoreSlim mutex = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly AegisIdentityTokenValidator validator = new();
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(15) };
    private OidcClient? client;
    private CancellationTokenSource? loginCancellation;
    private string? accessToken;
    private string? refreshToken;
    private DateTimeOffset validatedAt;
    private AccountState state;
    private volatile bool disposed;
    private bool restoreAttempted;

    public AegisAuthentication(AegisOptions options, IAegisSessionStore? sessionStore = null)
    {
        options.Validate();
        configuration = options;
        this.sessionStore = sessionStore ?? new SystemAegisSessionStore(options, WorkspaceStore.DataDirectory);
        state = new(false, false, options.Issuer, options.ClientId, options.RedirectUri);
    }

    public AccountState State => state;
    public long Generation { get; private set; }

    private OidcClient CreateClient() => new(new OidcClientOptions
    {
        Authority = configuration.Issuer, ClientId = configuration.ClientId,
        RedirectUri = configuration.RedirectUri, Scope = "openid profile email offline_access",
        IdentityTokenValidator = validator, FilterClaims = false,
        ClockSkew = TimeSpan.FromSeconds(30), StateLength = 32,
        RefreshDiscoveryOnSignatureFailure = true, HttpClientFactory = _ => http,
        Policy = new Policy
        {
            RequireIdentityTokenSignature = true, RequireAccessTokenHash = true,
            RequireIdentityTokenOnRefreshTokenResponse = true,
            ValidSignatureAlgorithms = new HashSet<string> { "RS256" },
            Discovery = new DiscoveryPolicy { RequireHttps = new Uri(configuration.Issuer).Scheme == "https" }
        }
    });

    private Task SaveSessionAsync(string subject)
    {
        var saved = new SavedAegisSession(configuration.Issuer, configuration.ClientId, subject, refreshToken!);
        return Task.Run(() => sessionStore.Write(saved));
    }

    private async Task<bool> InvalidateAsync()
    {
        ClearMemory();
        if (disposed) return true;
        try { await Task.Run(sessionStore.Delete); return true; }
        catch { return false; }
    }

    public async Task<AccountState> LoginAsync(Func<string, Task> openBrowser)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!await mutex.WaitAsync(0)) throw new InvalidOperationException("登录或账号验证正在进行，请稍后重试。");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(5));
        loginCancellation = cancellation;
        try
        {
            if (state.IsAuthenticated) return state;
            restoreAttempted = true;
            await Task.Run(sessionStore.Delete);
            state = state with { IsLoggingIn = true };
            using var callback = new LoopbackLoginCallback(configuration.RedirectUri);
            client = CreateClient();
            validator.ExpectedSubject = null;
            validator.ExpectedNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var login = await client.PrepareLoginAsync(new Parameters { { "nonce", validator.ExpectedNonce }, { "prompt", "login" } }, cancellation.Token);
            if (login.IsError) throw new IOException("无法读取 Aegis-IAM 登录配置，请检查服务地址和网络。");
            // 先监听，再打开浏览器；页面和原生壳都不会收到 code_verifier 或令牌。
            await openBrowser(login.StartUrl).WaitAsync(cancellation.Token);
            var response = await callback.ReceiveAsync(login.State, configuration.Issuer, cancellation.Token);
            var result = await client.ProcessResponseAsync(response, login, cancellationToken: cancellation.Token);
            if (result.IsError || string.IsNullOrEmpty(result.IdentityToken) || string.IsNullOrEmpty(result.RefreshToken) ||
                !string.Equals(result.TokenResponse.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                result.AccessTokenExpiration <= DateTimeOffset.UtcNow)
                throw new IOException("登录未完成或授权验证失败，请重新登录并同意所需权限。");
            cancellation.Token.ThrowIfCancellationRequested();
            var subject = result.User.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(subject)) throw new IOException("登录返回的账号资料无效。");
            accessToken = result.AccessToken;
            refreshToken = result.RefreshToken;
            validator.ExpectedSubject = subject;
            var profile = new AccountProfile(subject, result.User.FindFirst("name")?.Value ?? "",
                result.User.FindFirst("preferred_username")?.Value ?? "", result.User.FindFirst("email")?.Value ?? "");
            await SaveSessionAsync(subject);
            cancellation.Token.ThrowIfCancellationRequested();
            state = state with { IsAuthenticated = true, IsLoggingIn = false, User = profile, ExpiresAt = result.AccessTokenExpiration };
            validatedAt = DateTimeOffset.UtcNow;
            Generation++;
            return state;
        }
        catch (OperationCanceledException) { await InvalidateAsync(); throw new InvalidOperationException("登录已取消或超时，请重试。"); }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        { await InvalidateAsync(); throw new IOException("无法完成 Aegis-IAM 登录，请检查服务地址、网络和应用登记。"); }
        catch (IOException) { await InvalidateAsync(); throw; }
        catch { await InvalidateAsync(); throw new IOException("无法完成 Aegis-IAM 登录，请检查服务地址、网络和应用登记。"); }
        finally
        {
            validator.ExpectedNonce = null;
            loginCancellation = null;
            state = state with { IsLoggingIn = false };
            mutex.Release();
        }
    }

    public void CancelLogin() => loginCancellation?.Cancel();

    public async Task<AccountState> ValidateAsync(bool force = false)
    {
        if (disposed || restoreAttempted && !state.IsAuthenticated) return state;
        try { await mutex.WaitAsync(lifetime.Token); }
        catch (OperationCanceledException) { return state; }
        try
        {
            if (disposed) return state;
            if (!restoreAttempted)
            {
                restoreAttempted = true;
                await RestoreSessionAsync();
            }
            if (!state.IsAuthenticated) return state;
            var now = DateTimeOffset.UtcNow;
            if (state.ExpiresAt <= now.AddMinutes(1))
            {
                // 刷新令牌一次性消费；网络结果不确定时不重试同一个令牌。
                await Task.Run(sessionStore.Delete);
                var result = await client!.RefreshTokenAsync(refreshToken!, cancellationToken: lifetime.Token);
                if (result.IsError || string.IsNullOrEmpty(result.RefreshToken) || string.IsNullOrEmpty(result.IdentityToken) ||
                    result.AccessTokenExpiration <= now) { await InvalidateAsync(); return state; }
                lifetime.Token.ThrowIfCancellationRequested();
                accessToken = result.AccessToken;
                refreshToken = result.RefreshToken;
                state = state with { ExpiresAt = result.AccessTokenExpiration };
                force = true;
            }
            if (force || now - validatedAt >= TimeSpan.FromSeconds(60))
            {
                var profile = await client!.GetUserInfoAsync(accessToken!, lifetime.Token);
                if (profile.IsError || profile.Claims.FirstOrDefault(claim => claim.Type == "sub")?.Value != state.User!.Subject)
                { await InvalidateAsync(); return state; }
                lifetime.Token.ThrowIfCancellationRequested();
                await SaveSessionAsync(state.User.Subject);
                validatedAt = now;
            }
            return state;
        }
        catch (Exception)
        { await InvalidateAsync(); return state; }
        finally { mutex.Release(); }
    }

    private async Task RestoreSessionAsync()
    {
        var saved = await Task.Run(sessionStore.Read);
        if (saved is null) return;
        if (saved.Issuer != configuration.Issuer || saved.ClientId != configuration.ClientId ||
            string.IsNullOrWhiteSpace(saved.Subject) || string.IsNullOrWhiteSpace(saved.RefreshToken))
        { await InvalidateAsync(); return; }
        validator.ExpectedNonce = null;
        validator.ExpectedSubject = saved.Subject;
        client = CreateClient();
        // 只预加载并验证发现文档，不访问授权端点、不启动浏览器；此阶段失败保留尚未消费的凭据。
        try
        {
            var metadata = await client.PrepareLoginAsync(cancellationToken: lifetime.Token);
            if (metadata.IsError) return;
        }
        catch (Exception) { return; }
        lifetime.Token.ThrowIfCancellationRequested();
        // 先移除旧凭据，再消费一次性刷新令牌，防止崩溃或取消后重放旧令牌。
        await Task.Run(sessionStore.Delete);
        var result = await client.RefreshTokenAsync(saved.RefreshToken, cancellationToken: lifetime.Token);
        if (result.IsError || string.IsNullOrEmpty(result.IdentityToken) || string.IsNullOrEmpty(result.RefreshToken) ||
            string.IsNullOrEmpty(result.AccessToken) || result.AccessTokenExpiration <= DateTimeOffset.UtcNow)
            return;
        lifetime.Token.ThrowIfCancellationRequested();
        var userInfo = await client.GetUserInfoAsync(result.AccessToken, lifetime.Token);
        if (userInfo.IsError || userInfo.Claims.FirstOrDefault(claim => claim.Type == "sub")?.Value != saved.Subject) return;
        lifetime.Token.ThrowIfCancellationRequested();
        var profile = new AccountProfile(saved.Subject, userInfo.Claims.FirstOrDefault(claim => claim.Type == "name")?.Value ?? "",
            userInfo.Claims.FirstOrDefault(claim => claim.Type == "preferred_username")?.Value ?? "",
            userInfo.Claims.FirstOrDefault(claim => claim.Type == "email")?.Value ?? "");
        accessToken = result.AccessToken;
        refreshToken = result.RefreshToken;
        await SaveSessionAsync(saved.Subject);
        lifetime.Token.ThrowIfCancellationRequested();
        state = state with { IsAuthenticated = true, User = profile, ExpiresAt = result.AccessTokenExpiration };
        validatedAt = DateTimeOffset.UtcNow;
        Generation++;
    }

    public async Task<bool> LogoutAsync()
    {
        CancelLogin();
        await mutex.WaitAsync();
        try
        {
            SavedAegisSession? saved = null;
            var readSucceeded = true;
            if (refreshToken is null && accessToken is null)
            {
                try { saved = await Task.Run(sessionStore.Read); }
                catch { readSucceeded = false; }
            }
            restoreAttempted = true;
            var token = refreshToken ?? accessToken ?? (saved?.Issuer == configuration.Issuer && saved.ClientId == configuration.ClientId ? saved.RefreshToken : null);
            var hint = refreshToken is null ? "access_token" : "refresh_token";
            if (saved is not null) hint = "refresh_token";
            var cleared = await InvalidateAsync();
            if (token is null) return cleared && readSucceeded;
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var result = await http.RevokeTokenAsync(new TokenRevocationRequest
                {
                    Address = configuration.Issuer + "/oauth/revoke", ClientId = configuration.ClientId,
                    ClientCredentialStyle = ClientCredentialStyle.PostBody,
                    Token = token, TokenTypeHint = hint
                }, cancellation.Token);
                return cleared && readSucceeded && !result.IsError;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { return false; }
        }
        finally { mutex.Release(); }
    }

    private void ClearMemory()
    {
        accessToken = refreshToken = null;
        validator.ExpectedSubject = null;
        state = state with { IsAuthenticated = false, User = null, ExpiresAt = null };
        Generation++;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        CancelLogin();
        ClearMemory();
        http.Dispose();
        // 关闭程序只释放内存和会话锁；显式退出登录才删除系统凭据。
        sessionStore.Dispose();
    }
}
