using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using TermLume.Shared.Abstractions;
using TermLume.Shared.Application;
using TermLume.Shared.Services;

var passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + description);
    passed++; Console.WriteLine("PASS: " + description);
}
async Task Reject(Func<Task> action, string description)
{
    try { await action(); }
    catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException) { Check(true, description); return; }
    throw new InvalidOperationException("FAILED: " + description);
}
string RedirectUri()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    return $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/callback/";
}
var root = Path.Combine(Path.GetTempPath(), "TermLume-auth-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
await AuthenticationBoundaryTests.RunAsync(Path.Combine(root, "boundaries"), Check);
using var fixture = Process.Start(new ProcessStartInfo("node")
{
    ArgumentList = { Path.Combine(AppContext.BaseDirectory, "oidc-fixture.cjs") }, UseShellExecute = false,
    CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
}) ?? throw new IOException("Cannot start OIDC test fixture.");
var fixtureErrors = fixture.StandardError.ReadToEndAsync();
try
{
    var issuer = await fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) ?? throw new IOException("Missing fixture URL.");
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    AegisAuthentication Create() => new(new AegisOptions { Issuer = issuer, ClientId = "termlume-desktop", RedirectUri = RedirectUri() }, new MemorySessionStore());
    async Task Behavior(object value) { using var response = await http.PostAsJsonAsync(issuer + "/test/behavior", value); response.EnsureSuccessStatusCode(); }
    async Task<JsonElement> Stats() => await http.GetFromJsonAsync<JsonElement>(issuer + "/test/stats");
    Task? browserNavigation = null;
    Func<string, Task>? alterCallback = null;
    Task Browser(string url)
    {
        // 系统浏览器启动即返回；随后才会处理浏览器的回环请求。
        browserNavigation = Task.Run(async () =>
        {
            using var authorization = await http.GetAsync(url);
            Check(authorization.StatusCode == HttpStatusCode.Found, "Authorization endpoint accepts S256 PKCE and nonce");
            var location = authorization.Headers.Location!.AbsoluteUri;
            if (alterCallback is not null) await alterCallback(location);
            using var callback = await http.GetAsync(location);
            Check(callback.IsSuccessStatusCode, "Exact loopback callback is accepted");
        });
        return Task.CompletedTask;
    }
    async Task Login(AegisAuthentication authentication)
    {
        await authentication.LoginAsync(Browser).WaitAsync(TimeSpan.FromSeconds(20));
        if (browserNavigation is not null) await browserNavigation;
    }

    foreach (var options in new[] {
        new AegisOptions { Issuer = "http://remote.example.test" }, new AegisOptions { RedirectUri = "http://0.0.0.0:47831/callback/" },
        new AegisOptions { RedirectUri = "https://example.test/callback/" }, new AegisOptions { RedirectUri = "http://127.0.0.1:47831/callback/?code=forged" } })
        await Reject(() => { options.Validate(); return Task.CompletedTask; }, "Unsafe issuer or callback configuration is rejected");

    using var auth = Create();
    var host = new TestHost(Browser);
    var controller = new WorkspaceController(root, host, new TestDispatcher(), auth);
    async Task<JsonElement> Call(string operation, object? payload = null)
    {
        var id = Guid.NewGuid().ToString("N");
        await controller.HandleAsync(JsonSerializer.SerializeToElement(new { id, op = operation, payload = payload ?? new { } }, WorkspaceStore.JsonOptions)).WaitAsync(TimeSpan.FromSeconds(25));
        return host.Messages.Last(message => message.GetProperty("type").GetString() == "reply" && message.GetProperty("id").GetString() == id);
    }
    foreach (var operation in new[] { "workspace", "saveHost", "saveGroup", "connect", "sftpDefaultLocal", "sftpDropUpload", "logs", "exportLogs", "preferences", "copy", "paste", "pickKey" })
    {
        var denied = await Call(operation);
        Check(!denied.GetProperty("ok").GetBoolean() && denied.GetProperty("errorCode").GetString() == "authentication_required", "Backend denies unauthenticated " + operation);
    }
    Check((await Call("authStatus")).GetProperty("result").GetProperty("authentication").GetProperty("user").ValueKind == JsonValueKind.Null, "Pre-login bootstrap exposes no workspace or account");

    alterCallback = async location =>
    {
        var valid = new Uri(location);
        foreach (var invalid in new[] { location.Replace("state=", "state=forged"), location + "&state=duplicate", location.Replace(Uri.EscapeDataString(issuer), Uri.EscapeDataString("https://wrong.example.test")), location.Replace("/callback/", "/wrong/") })
        {
            Check(invalid != location, "Test callback differs from legitimate callback");
            using var response = await http.GetAsync(invalid);
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid state, duplicate parameter, issuer or path does not consume callback");
        }
    };
    Check((await Call("authLogin")).GetProperty("ok").GetBoolean(), "Controller completes browser login with signed ID token and UserInfo");
    if (browserNavigation is not null) await browserNavigation;
    alterCallback = null;
    Check(auth.State.User?.Subject == "synthetic-subject" && auth.State.User.Name == "Synthetic <account>", "Signed identity and UserInfo agree on subject");
    Check((await Call("saveGroup", new { name = "Authenticated group" })).GetProperty("ok").GetBoolean(), "Authenticated workspace operations are enabled");
    var bootstrap = (await Call("authStatus")).GetRawText();
    Check(!bootstrap.Contains("access_token") && !bootstrap.Contains("refresh_token") && !bootstrap.Contains("id_token"), "Desktop bridge never receives tokens");

    // 退出与等待原生凭据的建连请求竞争时，旧请求不能恢复会话。
    var saved = await Call("saveHost", new { name = "Test", address = "127.0.0.1", port = 9, username = "test" });
    var hostId = saved.GetProperty("result").GetProperty("hosts")[0].GetProperty("id").GetString();
    host.PendingCredential = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var connection = Call("connect", new { hostId, sessionId = Guid.NewGuid().ToString("N") });
    await host.CredentialRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check((await Call("authLogout")).GetProperty("result").GetProperty("revocationSucceeded").GetBoolean(), "Logout revokes application grant");
    host.PendingCredential.SetResult("synthetic-secret");
    Check((await connection).GetProperty("errorCode").GetString() == "authentication_required" && controller.Status.Ssh == 0, "Logout prevents a pending credential prompt from creating a connection");
    Check(!(await Call("workspace")).GetProperty("ok").GetBoolean(), "Logout immediately locks workspace");

    foreach (var bad in new[] { "signature", "audience", "issuer", "nonce", "expired", "hash", "missing_id_token", "userinfo" })
    {
        await Behavior(new { bad });
        using var rejected = Create();
        await Reject(() => Login(rejected), "Rejects invalid login: " + bad);
        Check(!rejected.State.IsAuthenticated, "Invalid identity never unlocks workspace");
    }
    await Behavior(new { deny = true });
    using (var denied = Create()) await Reject(() => Login(denied), "Consent denial returns to signed-out state");

    await Behavior(new { shortExpiry = true });
    using (var renewable = Create())
    {
        await Login(renewable);
        var before = (await Stats()).GetProperty("requests").EnumerateArray().Count(row => row.GetProperty("path").GetString() == "/oauth/token");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => renewable.ValidateAsync(force: true)));
        var after = (await Stats()).GetProperty("requests").EnumerateArray().Count(row => row.GetProperty("path").GetString() == "/oauth/token");
        Check(renewable.State.IsAuthenticated && after == before + 1, "Concurrent validation rotates refresh token exactly once");
        await Behavior(new { revoke = true });
        Check(!(await renewable.ValidateAsync(force: true)).IsAuthenticated, "Revoked server session locks client after UserInfo validation");
    }
    await Behavior(new { });
    using (var cancelledBrowser = Create())
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = cancelledBrowser.LoginAsync(_ => { opened.SetResult(); return new TaskCompletionSource().Task; });
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelledBrowser.CancelLogin();
        await Reject(() => pending.WaitAsync(TimeSpan.FromSeconds(2)), "Cancelling an unresponsive browser launch returns immediately");
    }
    await Behavior(new { shortExpiry = true });
    using (var changedSubject = Create())
    {
        await Login(changedSubject);
        await Behavior(new { bad = "subject" });
        Check(!(await changedSubject.ValidateAsync(force: true)).IsAuthenticated, "Refresh cannot change the signed-in subject");
    }
    await Behavior(new { });
    using (var cancelled = Create())
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = cancelled.LoginAsync(_ => { opened.SetResult(); return Task.CompletedTask; });
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.CancelLogin();
        await Reject(() => pending, "Login cancellation closes callback listener");
        await Login(cancelled);
        Check(cancelled.State.IsAuthenticated, "Cancelled login can immediately be retried on the same port");
        await Behavior(new { revokeFailure = true });
        Check(!await cancelled.LogoutAsync() && !cancelled.State.IsAuthenticated, "Failed server revocation still clears local login");
    }
    using (var occupied = new TcpListener(IPAddress.Loopback, 0))
    {
        occupied.Start();
        using var blocked = new AegisAuthentication(new AegisOptions { Issuer = issuer, RedirectUri = $"http://127.0.0.1:{((IPEndPoint)occupied.LocalEndpoint).Port}/callback/" }, new MemorySessionStore());
        await Reject(() => blocked.LoginAsync(_ => throw new InvalidOperationException("Must not open browser")), "Occupied callback port reports failure before opening browser");
    }
    Check(Directory.GetFiles(root, "*.json").All(path => !File.ReadAllText(path).Contains("synthetic-secret") && !File.ReadAllText(path).Contains("refresh_token")), "Credentials and tokens never enter workspace JSON files");
    await controller.ShutdownAsync();
    await SessionPersistenceTests.RunAsync(root, new AegisOptions { Issuer = issuer, ClientId = "termlume-desktop", RedirectUri = RedirectUri() }, Login, Behavior, Stats, Check);
    Console.WriteLine($"PASS: {passed} authentication checks; isolated data: {root}");
}
finally
{
    fixture.StandardInput.Close();
    try { await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { fixture.Kill(true); }
    var errors = await fixtureErrors;
    if (fixture.ExitCode != 0 || !string.IsNullOrWhiteSpace(errors)) throw new IOException("OIDC fixture failed: " + errors);
}

sealed class TestHost(Func<string, Task> browser) : IWorkspaceHost
{
    public List<JsonElement> Messages { get; } = [];
    public TaskCompletionSource<string?>? PendingCredential { get; set; }
    public TaskCompletionSource CredentialRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Send(object message) => Messages.Add(JsonSerializer.SerializeToElement(message, WorkspaceStore.JsonOptions));
    public async Task<T?> RequestAsync<T>(string operation, object? payload = null)
    {
        if (operation == "openBrowser")
        {
            await browser(JsonSerializer.SerializeToElement(payload).GetProperty("url").GetString()!);
            return (T?)(object)true;
        }
        if (operation == "credential" && PendingCredential is not null)
        {
            CredentialRequested.TrySetResult();
            return (T?)(object?)(await PendingCredential.Task);
        }
        throw new InvalidOperationException("Unexpected native operation: " + operation);
    }
}
sealed class TestDispatcher : IWorkspaceDispatcher
{
    public void BeginInvoke(Action action) => action();
    public void Invoke(Action action) => action();
    public T Invoke<T>(Func<T> action) => action();
}
