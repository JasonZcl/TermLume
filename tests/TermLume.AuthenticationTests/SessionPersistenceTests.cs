using System.Text.Json;
using TermLume.Shared.Abstractions;
using TermLume.Shared.Services;

internal static class SessionPersistenceTests
{
    public static async Task RunAsync(string root, AegisOptions options, Func<AegisAuthentication, Task> login,
        Func<object, Task> behavior, Func<Task<JsonElement>> stats, Action<bool, string> check)
    {
        await behavior(new { });
        var cache = new MemorySessionStore();
        using (var first = new AegisAuthentication(options, cache))
        {
            await login(first);
            check(cache.Session?.Subject == first.State.User?.Subject && !string.IsNullOrEmpty(cache.Session?.RefreshToken),
                "Successful login saves a refresh credential bound to the signed-in subject");
        }
        var originalToken = cache.Session?.RefreshToken;
        check(originalToken is not null, "Closing the application preserves the saved login");
        var before = CountTokens(await stats());
        using (var reopened = new AegisAuthentication(options, cache))
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => reopened.ValidateAsync()));
            check(reopened.State.IsAuthenticated && reopened.State.User?.Subject == "synthetic-subject",
                "Reopening restores the account without an interactive browser login");
            check(CountTokens(await stats()) == before + 1 && cache.Session?.RefreshToken != originalToken,
                "Concurrent startup checks consume the stored refresh token exactly once and save its replacement");
            check(await reopened.LogoutAsync() && cache.Session is null, "Explicit logout clears the saved credential");
        }
        using (var signedOut = new AegisAuthentication(options, cache))
            check(!(await signedOut.ValidateAsync()).IsAuthenticated, "Reopening after explicit logout remains signed out");

        using (var first = new AegisAuthentication(options, cache)) await login(first);
        var revocations = CountRevocations(await stats());
        using (var coldLogout = new AegisAuthentication(options, cache))
            check(await coldLogout.LogoutAsync() && cache.Session is null && CountRevocations(await stats()) == revocations + 1,
                "Logout before restoring also revokes the saved server grant");

        using (var first = new AegisAuthentication(options, cache)) await login(first);
        await behavior(new { discoveryFailure = true });
        revocations = CountRevocations(await stats());
        using (var offlineLogout = new AegisAuthentication(options, cache))
        {
            await offlineLogout.ValidateAsync();
            check(await offlineLogout.LogoutAsync() && cache.Session is null && CountRevocations(await stats()) == revocations + 1,
                "Logout after failed discovery still revokes the unconsumed saved grant");
        }
        await behavior(new { });
        cache.Session = new(options.Issuer, options.ClientId, "synthetic-subject", "synthetic-refresh");
        cache.ReadFails = true;
        using (var unreadable = new AegisAuthentication(options, cache))
            check(!await unreadable.LogoutAsync() && cache.Session is null && !unreadable.State.IsAuthenticated,
                "A credential read failure still clears local login and reports incomplete revocation");
        cache.ReadFails = false;

        foreach (var bad in new[] { "subject", "signature", "audience", "userinfo" })
        {
            await behavior(new { });
            using (var first = new AegisAuthentication(options, cache)) await login(first);
            await behavior(new { bad });
            using var reopened = new AegisAuthentication(options, cache);
            check(!(await reopened.ValidateAsync()).IsAuthenticated && cache.Session is null,
                "Restoration rejects and clears invalid identity: " + bad);
        }
        await behavior(new { });
        using (var first = new AegisAuthentication(options, cache)) await login(first);
        await behavior(new { revoke = true });
        using (var revoked = new AegisAuthentication(options, cache))
            check(!(await revoked.ValidateAsync()).IsAuthenticated && cache.Session is null, "Server revocation prevents restoring a saved login");

        await behavior(new { });
        using (var first = new AegisAuthentication(options, cache)) await login(first);
        await behavior(new { discoveryFailure = true });
        using (var offline = new AegisAuthentication(options, cache))
            check(!(await offline.ValidateAsync()).IsAuthenticated && cache.Session is not null,
                "An unavailable discovery endpoint keeps workspace locked and preserves an unconsumed login");
        await behavior(new { });
        using (var recovered = new AegisAuthentication(options, cache))
        {
            check((await recovered.ValidateAsync()).IsAuthenticated, "A later launch restores login when the IAM endpoint is available again");
            await recovered.LogoutAsync();
        }
        cache.Session = new("https://another-issuer.example", options.ClientId, "synthetic-subject", "synthetic-refresh");
        using (var wrongIssuer = new AegisAuthentication(options, cache))
            check(!(await wrongIssuer.ValidateAsync()).IsAuthenticated && cache.Session is null, "A saved credential cannot cross issuer boundaries");

        var directory = Path.Combine(root, "system-credential-store");
        var synthetic = new SavedAegisSession(options.Issuer, options.ClientId, "vault-test-subject", "vault-probe-" + Guid.NewGuid().ToString("N"));
        using (var first = new SystemAegisSessionStore(options, directory))
        {
            first.Write(synthetic);
            using var competing = new SystemAegisSessionStore(options, directory);
            try { competing.Read(); throw new InvalidOperationException("Concurrent ownership was accepted"); }
            catch (IOException) { check(true, "A second process cannot consume a refresh credential owned by a running instance"); }
        }
        using (var reopened = new SystemAegisSessionStore(options, directory))
        {
            try
            {
                check(reopened.Read() == synthetic, "The operating system credential store survives disposal and reopening");
                check(Directory.GetFiles(directory, "*.enabled").All(path => !File.ReadAllText(path).Contains(synthetic.RefreshToken)),
                    "Workspace files contain no plaintext refresh credential");
            }
            finally { reopened.Delete(); }
        }
        using (var cleared = new SystemAegisSessionStore(options, directory))
            check(cleared.Read() is null, "Deleting an operating system credential persists across reopening");
    }

    private static int CountTokens(JsonElement stats) => stats.GetProperty("requests").EnumerateArray().Count(row => row.GetProperty("path").GetString() == "/oauth/token");
    private static int CountRevocations(JsonElement stats) => stats.GetProperty("requests").EnumerateArray().Count(row => row.GetProperty("path").GetString() == "/oauth/revoke");
}
