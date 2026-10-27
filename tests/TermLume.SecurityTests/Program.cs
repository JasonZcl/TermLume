using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using TermLume.Models;
using TermLume.Services;

var root = Path.Combine(Path.GetTempPath(), "TermLume-security-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + description);
    passed++;
    Console.WriteLine("PASS: " + description);
}
void Reject(Action action, string description)
{
    try { action(); }
    catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
    { Check(true, description); return; }
    throw new InvalidOperationException("FAILED: " + description);
}
async Task RejectAsync(Func<Task> action, string description)
{
    try { await action(); }
    catch (IOException) { Check(true, description); return; }
    throw new InvalidOperationException("FAILED: " + description);
}
Check(Environment.Version == new Version(10, 0, 14), "Regressions execute on the pinned .NET 10.0.14 runtime");

var freshPreferences = PreferenceSettings.Normalize(new Preferences());
Check(freshPreferences.Shortcuts["snippets"] == "Ctrl+Shift+KeyS", "New profiles receive the snippets shortcut");
var upgradedPreferences = PreferenceSettings.Normalize(new Preferences { Shortcuts = new() { ["newHost"] = "Ctrl+Shift+KeyS", ["searchHosts"] = "Ctrl+Alt+KeyK" } });
Check(upgradedPreferences.Shortcuts["snippets"] == "" && upgradedPreferences.Shortcuts["newHost"] == "Ctrl+Shift+KeyS" && upgradedPreferences.Shortcuts["searchHosts"] == "Ctrl+Alt+KeyK", "Adding snippets never resets conflicting legacy custom shortcuts");
var disabledSnippetShortcut = PreferenceSettings.Normalize(new Preferences { Shortcuts = new() { ["snippets"] = "" } }, strict: true);
Check(disabledSnippetShortcut.Shortcuts["snippets"] == "", "Users can disable the snippets shortcut");

var store = new WorkspaceStore(Path.Combine(root, "workspace"));
var workspace = new Workspace();
var registry = new KnownHostRegistry(workspace, store);
var host = new HostProfile { Address = "EXAMPLE.test", Port = 22, Username = "audit" };
Check(registry.Verify(host, "ssh-ed25519", "original") == HostKeyStatus.Unknown, "A genuinely new endpoint uses TOFU");
registry.Trust(host, "ssh-ed25519", "original");
Check(registry.Verify(host, "ssh-ed25519", "original") == HostKeyStatus.Trusted, "Existing trusted key remains valid");
Check(registry.Verify(host, "ssh-ed25519", "changed") == HostKeyStatus.Changed, "Changed key is rejected");
var saved = File.ReadAllBytes(Path.Combine(root, "workspace", "workspace.json"));
Check(registry.Verify(host, "rsa-sha2-512", "attacker") == HostKeyStatus.AlgorithmChanged, "Changing algorithms is not a first connection");
Reject(() => registry.Trust(host, "rsa-sha2-512", "attacker"), "Trust cannot bypass the existing endpoint identity");
Check(saved.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "workspace", "workspace.json"))), "Algorithm substitution preserves the workspace byte-for-byte");
Check(workspace.KnownHosts.Count == 1, "Algorithm substitution adds no trust record");
Check(registry.Verify(new HostProfile { Address = "example.TEST", Port = 22 }, "rsa-sha2-512", "attacker") == HostKeyStatus.AlgorithmChanged, "Host casing cannot bypass the algorithm guard");
Check(registry.Verify(new HostProfile { Address = "example.test", Port = 2222 }, "rsa-sha2-512", "new") == HostKeyStatus.Unknown, "Independent ports remain separate identities");
workspace.KnownHosts[KnownHostRegistry.Endpoint(host, "rsa-sha2-256")] = "legacy-rsa";
Check(registry.Verify(host, "rsa-sha2-256", "legacy-rsa") == HostKeyStatus.Trusted, "Previously saved multi-algorithm identities remain compatible");
registry.Forget(KnownHostRegistry.Endpoint(host, "ssh-ed25519"));
Check(registry.Verify(host, "ssh-ed25519", "new") == HostKeyStatus.AlgorithmChanged, "Removing one algorithm does not reset other saved identities");
registry.Forget(KnownHostRegistry.Endpoint(host, "rsa-sha2-256"));
Check(registry.Verify(host, "ssh-ed25519", "new") == HostKeyStatus.Unknown, "Explicitly removing all endpoint identities permits re-enrollment");
registry.Trust(host, "ssh-ed25519", "new");
Check(new WorkspaceStore(Path.Combine(root, "workspace")).Load().KnownHosts.Count == 1, "Explicit trust update persists");
var blockedDirectory = Path.Combine(root, "blocked-store");
File.WriteAllText(blockedDirectory, "synthetic obstruction");
var failedWorkspace = new Workspace();
Reject(() => new KnownHostRegistry(failedWorkspace, new WorkspaceStore(blockedDirectory)).Trust(host, "ssh-ed25519", "new"), "Failed trust persistence reports an error");
Check(failedWorkspace.KnownHosts.Count == 0, "Failed trust persistence rolls back memory");

var python = Environment.GetEnvironmentVariable("TERMLUME_TEST_PYTHON") ?? "python";
var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ssh_fixture.py"));
start.ArgumentList.Add(root);
using var fixture = Process.Start(start) ?? throw new InvalidOperationException("Could not start the loopback fixture. Install Paramiko in the test Python environment.");
var fixtureErrors = fixture.StandardError.ReadToEndAsync();
var fixtureOutput = fixture.StandardOutput.ReadToEndAsync();
try
{
    var portFile = Path.Combine(root, "port.txt");
    for (var attempt = 0; !File.Exists(portFile) && attempt < 100; attempt++)
    {
        if (fixture.HasExited) throw new InvalidOperationException(await fixtureErrors);
        await Task.Delay(100);
    }
    var port = int.Parse(File.ReadAllText(portFile));
    HostProfile Server(string username) => new() { Address = "127.0.0.1", Port = port, Username = username };
    Check(SshSession.MaximumBufferedOutput == 4 * 1024 * 1024, "The dependency enforces a 4 MiB receive limit");
    Check(typeof(SshClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.StartsWith("2026.0.0.2", StringComparison.Ordinal), "The patched SSH.NET assembly is loaded");
    var echo = new StringBuilder();
    using var healthy = new SshSession(Server("echo"), "test-only", _ => true);
    var healthyEnded = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    healthy.Ended += error => healthyEnded.TrySetResult(error);
    healthy.Output += text => { lock (echo) echo.Append(text); healthy.AcknowledgeOutput(); };
    await healthy.ConnectAsync(80, 24).WaitAsync(TimeSpan.FromSeconds(15));

    using (var flooded = new SshSession(Server("flood"), "test-only", _ => true))
    {
        var ended = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var endCount = 0;
        var outputCount = 0;
        flooded.Ended += error => { Interlocked.Increment(ref endCount); ended.TrySetResult(error); };
        flooded.Output += _ => Interlocked.Increment(ref outputCount);
        await flooded.ConnectAsync(80, 24).WaitAsync(TimeSpan.FromSeconds(15));
        var stream = (ShellStream)typeof(SshSession).GetField("shell", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(flooded)!;
        string? error;
        try { error = await ended.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException) { Console.Error.WriteLine($"Flood timeout: buffered={stream.Length}, output={outputCount}, ended={endCount}"); throw; }
        Check(error?.Contains("安全缓冲", StringComparison.Ordinal) == true, "A flood closes the session even while the frontend ACK is withheld");
        Check(stream.Length <= SshSession.MaximumBufferedOutput, "Receive buffering never exceeds the configured cap");
        await Task.Delay(100);
        Check(endCount == 1 && outputCount == 1, "Flood shutdown emits one ending event and does not flood the frontend");
    }

    using (var startup = new SshSession(Server("startup-flood"), "test-only", _ => true))
    {
        var rejected = false;
        try { await startup.ConnectAsync(80, 24).WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception exception) when (exception is Renci.SshNet.Common.SshException or OperationCanceledException or ObjectDisposedException) { rejected = true; }
        Check(rejected, "The receive cap also protects pending PTY creation");
    }

    const string payload = "中文 SSH echo ✓\n";
    Check(healthy.Write(payload), "A separate healthy session still accepts input after floods");
    healthy.Resize(117, 39);
    for (var attempt = 0; attempt < 100; attempt++)
    {
        lock (echo) if (echo.ToString().Contains(payload, StringComparison.Ordinal)) break;
        await Task.Delay(50);
    }
    lock (echo) Check(echo.ToString().Contains(payload, StringComparison.Ordinal), "Unicode terminal output survives split UTF-8 packets");
    for (var attempt = 0; !File.Exists(Path.Combine(root, "resized.txt")) && attempt < 100; attempt++) await Task.Delay(50);
    Check(File.ReadAllText(Path.Combine(root, "resized.txt")) == "117x39", "PTY resizing still works");

    using var sftp = new SftpSession(Server("sftp"), "test-only", _ => true);
    await sftp.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(15));
    var localDirectory = Path.Combine(root, "local");
    Directory.CreateDirectory(localDirectory);
    var source = Path.Combine(localDirectory, "source.txt");
    var destination = Path.Combine(localDirectory, "download.txt");
    File.WriteAllText(source, "SFTP 中文 round-trip ✓");
    var browser = new LocalFileBrowser(localDirectory);
    await sftp.TransferAsync(true, source, "/round-trip.txt", (_, _) => { }, CancellationToken.None, () => browser.Validate(source));
    Check((await sftp.ListAsync("/")).Entries.Any(entry => entry.Name == "round-trip.txt"), "SFTP upload and listing work with the patched assembly");
    await sftp.TransferAsync(false, destination, "/round-trip.txt", (_, _) => { }, CancellationToken.None, () => browser.Validate(localDirectory));
    Check(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(destination)), "SFTP download preserves bytes");
    await RejectAsync(() => sftp.TransferAsync(true, source, "/round-trip.txt", (_, _) => { }, CancellationToken.None), "SFTP upload refuses to overwrite an existing file");
    await RejectAsync(() => sftp.TransferAsync(false, destination, "/round-trip.txt", (_, _) => { }, CancellationToken.None), "SFTP download refuses to overwrite an existing file");
    await sftp.MutateAsync("rename", "/round-trip.txt", "renamed.txt");
    await sftp.MutateAsync("delete", "/renamed.txt", null);
    Check(!(await sftp.ListAsync("/")).Entries.Any(entry => entry.Name == "renamed.txt"), "SFTP rename and delete retain their behavior");
    healthy.Write("finish\n");
    Check(await healthyEnded.Task.WaitAsync(TimeSpan.FromSeconds(10)) is null, "A normal terminal EOF remains a normal closure");
    lock (echo) Check(echo.ToString().EndsWith("最后一行 ✓\n", StringComparison.Ordinal), "Normal EOF preserves trailing buffered output");
    foreach (var encrypted in new[] { false, true })
    {
        var keyHost = Server("echo");
        keyHost.AuthType = "key";
        keyHost.PrivateKeyPath = Path.Combine(root, encrypted ? "client-encrypted.key" : "client.key");
        using var keySession = new SshSession(keyHost, encrypted ? "synthetic-passphrase" : "", _ => true);
        var keyEnded = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        keySession.Output += _ => keySession.AcknowledgeOutput();
        keySession.Ended += error => keyEnded.TrySetResult(error);
        await keySession.ConnectAsync(80, 24).WaitAsync(TimeSpan.FromSeconds(15));
        keySession.Write("finish\n");
        Check(await keyEnded.Task.WaitAsync(TimeSpan.FromSeconds(10)) is null, encrypted ? "Encrypted RSA keys retain SHA-2 authentication" : "Unencrypted RSA keys retain SHA-2 authentication");
    }
    using (var unresponsive = new SshSession(Server("quiet"), "test-only", _ => true))
    {
        var unresponsiveEnded = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        unresponsive.Ended += error => unresponsiveEnded.TrySetResult(error);
        unresponsive.Output += _ => { };
        await unresponsive.ConnectAsync(80, 24).WaitAsync(TimeSpan.FromSeconds(15));
        Check((await unresponsiveEnded.Task.WaitAsync(TimeSpan.FromSeconds(35)))?.Contains("响应超时", StringComparison.Ordinal) == true, "Missing renderer ACKs also time out when output stays below the receive cap");
    }
    Console.WriteLine($"{passed} security regressions passed. Isolated data: {root}");
}
finally
{
    if (!fixture.HasExited) await fixture.StandardInput.WriteLineAsync("stop");
    try { await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    catch (TimeoutException) { fixture.Kill(entireProcessTree: true); await fixture.WaitForExitAsync(); }
    var errors = await fixtureErrors;
    if (!string.IsNullOrWhiteSpace(errors)) Console.Error.WriteLine(errors);
    Console.Write(await fixtureOutput);
}
