using System.Collections.Concurrent;
using System.Text.Json;
using TermLume.Shared.Abstractions;
using TermLume.Shared.Application;
using TermLume.Shared.Services;

internal static class AuthenticationBoundaryTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        foreach (var replaceAccount in new[] { false, true })
        {
            var directory = Path.Combine(root, replaceAccount ? "account-replaced" : "logout");
            using var authentication = new DelayedAuthentication();
            var host = new RecordingHost();
            var controller = new WorkspaceController(directory, host, new Dispatcher(), authentication, new LocalTestWorkspaceSynchronization());
            Task? logout = null;
            try
            {
                var snapshot = authentication.State;
                authentication.Validation = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var request = controller.HandleAsync(Request("stale-mutation", "saveGroup", new { name = "Must not be saved" }));
                if (replaceAccount) authentication.ReplaceAccount();
                else
                {
                    logout = controller.HandleAsync(Request("logout", "authLogout", new { }));
                    await authentication.LogoutStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                }
                authentication.Validation.SetResult(snapshot);
                await request.WaitAsync(TimeSpan.FromSeconds(2));
                var reply = host.Messages.Single(message => message.GetProperty("type").GetString() == "reply" && message.GetProperty("id").GetString() == "stale-mutation");
                check(!new WorkspaceStore(directory).Load().Groups.Contains("Must not be saved"),
                    replaceAccount ? "A stale validation cannot mutate workspace after the account changes" : "Logout during asynchronous validation prevents all subsequent disk mutations");
                check(!reply.GetProperty("ok").GetBoolean() && reply.GetProperty("errorCode").GetString() == "authentication_required",
                    "An obsolete authentication generation returns an authorization error");
                if (!replaceAccount)
                {
                    await controller.HandleAsync(Request("status-during-logout", "authStatus", new { }));
                    var status = host.Messages.Single(message => message.GetProperty("type").GetString() == "reply" && message.GetProperty("id").GetString() == "status-during-logout");
                    check(!status.GetProperty("result").GetProperty("authentication").GetProperty("isAuthenticated").GetBoolean(),
                        "Status remains signed out while validation still holds the old authenticated state");
                }
            }
            finally
            {
                authentication.LogoutCompletion.TrySetResult(true);
                if (logout is not null) await logout.WaitAsync(TimeSpan.FromSeconds(2));
                await controller.ShutdownAsync();
            }
        }
        await NativeCapabilitiesAsync(Path.Combine(root, "native"), check);
    }

    private static JsonElement Request(string id, string op, object payload) => JsonSerializer.SerializeToElement(new { id, op, payload });

    private static async Task NativeCapabilitiesAsync(string directory, Action<bool, string> check)
    {
        using var authentication = new DelayedAuthentication();
        var nativeCalls = new List<string>();
        var host = new RecordingHost { NativeRequest = (operation, _) =>
        {
            nativeCalls.Add(operation);
            return Task.FromResult<object?>(operation switch { "copy" => true, "paste" => "synthetic clipboard", "pickKey" => null, _ => throw new InvalidOperationException(operation) });
        } };
        var controller = new WorkspaceController(directory, host, new Dispatcher(), authentication, new LocalTestWorkspaceSynchronization());
        async Task<JsonElement> Call(string operation, object? payload = null)
        {
            var id = Guid.NewGuid().ToString("N");
            await controller.HandleAsync(Request(id, operation, payload ?? new { })).WaitAsync(TimeSpan.FromSeconds(2));
            return host.Messages.Single(message => message.GetProperty("type").GetString() == "reply" && message.GetProperty("id").GetString() == id);
        }
        try
        {
            check((await Call("copy", new { text = "synthetic" })).GetProperty("result").GetBoolean(), "Authenticated clipboard writes use the native adapter");
            check((await Call("paste")).GetProperty("result").GetString() == "synthetic clipboard", "Authenticated clipboard reads use the native adapter");
            check((await Call("pickKey")).GetProperty("result").ValueKind == JsonValueKind.Null, "Cancelling an authenticated key picker returns null");
            foreach (var invalid in new object[] { new { }, new { text = 5 }, new { text = new string('x', 1024 * 1024 + 1) } })
                check(!(await Call("copy", invalid)).GetProperty("ok").GetBoolean(), "Invalid clipboard input is rejected before native execution");
            check(nativeCalls.Count == 3, "Malformed requests never reach the native clipboard");

            var keyPicker = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.NativeRequest = (_, _) => keyPicker.Task;
            var pending = Call("pickKey");
            authentication.ReplaceAccount();
            keyPicker.SetResult("synthetic-private-key-path");
            var staleReply = await pending;
            check(!staleReply.GetProperty("ok").GetBoolean() && staleReply.GetProperty("errorCode").GetString() == "authentication_required" &&
                !staleReply.TryGetProperty("result", out _), "Account changes discard private key paths from an earlier dialog");

            authentication.LogoutCompletion.TrySetResult(true);
            await Call("authLogout");
            host.NativeRequest = (_, _) => throw new InvalidOperationException("Unauthenticated native operation reached the adapter");
            foreach (var operation in new[] { "copy", "paste", "pickKey" })
            {
                var denied = await Call(operation, new { text = "synthetic" });
                check(!denied.GetProperty("ok").GetBoolean() && denied.GetProperty("errorCode").GetString() == "authentication_required",
                    "Native " + operation + " requires a current login");
            }
        }
        finally { await controller.ShutdownAsync(); }
    }

    private sealed class DelayedAuthentication : IWorkspaceAuthentication
    {
        public AccountState State { get; private set; } = new(true, false, "https://iam.example.test", "test-client", "http://127.0.0.1:47831/callback/", new("first-account", "First", "first", ""));
        public long Generation { get; private set; } = 1;
        public TaskCompletionSource<AccountState>? Validation { get; set; }
        public TaskCompletionSource LogoutStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> LogoutCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AccountState> ValidateAsync(bool force = false) => Validation?.Task ?? Task.FromResult(State);
        public Task<AccountState> LoginAsync(Func<string, Task> openBrowser) => Task.FromResult(State);
        public void CancelLogin() { }
        public async Task<bool> LogoutAsync()
        {
            LogoutStarted.TrySetResult();
            // 模拟真实认证服务等待验证互斥锁后才清除令牌的情况。
            var result = await LogoutCompletion.Task;
            State = State with { IsAuthenticated = false, User = null }; Generation++;
            return result;
        }
        public void ReplaceAccount() { State = State with { User = new("second-account", "Second", "second", "") }; Generation++; }
        public void Dispose() { }
    }

    private sealed class RecordingHost : IWorkspaceHost
    {
        public ConcurrentQueue<JsonElement> Messages { get; } = new();
        public Func<string, object?, Task<object?>>? NativeRequest { get; set; }
        public void Send(object message) => Messages.Enqueue(JsonSerializer.SerializeToElement(message, WorkspaceStore.JsonOptions));
        public async Task<T?> RequestAsync<T>(string operation, object? payload = null)
        {
            var result = await (NativeRequest ?? throw new InvalidOperationException("Unexpected native operation"))(operation, payload);
            return result is null ? default : (T)result;
        }
    }
    private sealed class Dispatcher : IWorkspaceDispatcher
    {
        public void BeginInvoke(Action action) => action();
        public void Invoke(Action action) => action();
        public T Invoke<T>(Func<T> action) => action();
    }
}
