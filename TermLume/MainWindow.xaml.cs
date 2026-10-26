using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using Localization = TermLume.Services.Localization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Renci.SshNet.Common;
using TermLume.Models;
using TermLume.Services;
using TermLume.Views;

namespace TermLume;

public partial class MainWindow : Window
{
    private const string AppOrigin = "https://termlume.local";
    private readonly WorkspaceStore store = new();
    private readonly Dictionary<string, SshSession> sessions = [];
    private Workspace workspace = new();
    private bool shuttingDown;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += InitializeAsync;
        Closing += OnClosing;
        StartupClose.Click += (_, _) => Close();
        StateChanged += (_, _) => Post(new { type = "windowState", maximized = WindowState == WindowState.Maximized });
    }

    private async void InitializeAsync(object sender, RoutedEventArgs args)
    {
        try
        {
            workspace = store.Load();
            Localization.Language = workspace.Preferences.Language;
            Title = Localization.Text("TermLume · SSH 工作空间");
            StartupMessage.Text = Localization.Text("正在准备你的工作空间…");
            StartupClose.Content = Localization.Text("关闭");
            Record("application.started", "TermLume 已启动。", category: "application");
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(WorkspaceStore.DataDirectory, "WebView2"));
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("termlume.local", Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsNonClientRegionSupportEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.NavigationStarting += (_, navigation) => navigation.Cancel = navigation.Uri != AppOrigin + "/index.html";
            core.NewWindowRequested += (_, request) => request.Handled = true;
            core.PermissionRequested += (_, request) => request.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, download) => download.Cancel = true;
            core.WebMessageReceived += OnMessage;
            core.NavigationCompleted += (_, navigation) =>
            {
                if (navigation.IsSuccess)
                {
                    StartupMessage.Visibility = Visibility.Collapsed;
                    StartupClose.Visibility = Visibility.Collapsed;
                }
                else StartupMessage.Text = Localization.Text("界面加载失败：") + navigation.WebErrorStatus;
            };
            Browser.Source = new Uri(AppOrigin + "/index.html");
        }
        catch (Exception exception)
        {
            StartupMessage.Text = Localization.Text("启动失败：") + Localization.Text(exception.Message) + Localization.Text("\n请检查 WebView2 Runtime 与本地工作空间文件。");
            StartupMessage.TextWrapping = TextWrapping.Wrap;
            StartupMessage.Margin = new Thickness(40);
        }
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (args.Source != AppOrigin + "/index.html" || shuttingDown) return;
        string? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var request = document.RootElement;
            requestId = request.GetProperty("id").GetString();
            var operation = request.GetProperty("op").GetString();
            var payload = request.GetProperty("payload");
            object? result = operation switch
            {
                "sftpDropUpload" => await StartDroppedUploadsAsync(payload.Clone(), args.AdditionalObjects),
                "workspace" => workspace,
                "knownHosts" => KnownHosts.Entries(),
                "logs" => activityLog.Snapshot(),
                "clearLogs" => activityLog.Clear(),
                "exportLogs" => ExportLogs(payload),
                "window" => ControlWindow(payload.GetProperty("action").GetString()),
                "saveGroup" => SaveGroup(payload),
                "saveHost" => SaveHost(payload),
                "deleteHost" => DeleteHost(payload.GetProperty("id").GetString()!),
                "favorite" => ToggleFavorite(payload.GetProperty("id").GetString()!),
                "saveSnippet" => SaveSnippet(payload),
                "deleteSnippet" => DeleteSnippet(payload.GetProperty("id").GetString()!),
                "preferences" => SavePreferences(payload),
                "forgetKey" => ForgetKey(payload.GetProperty("endpoint").GetString()!),
                "pickKey" => PickKey(),
                "connect" => await ConnectAsync(payload.Clone()),
                "disconnect" => Disconnect(payload.GetProperty("sessionId").GetString()!),
                "input" => WriteInput(payload),
                "resize" => Resize(payload),
                "ack" => Acknowledge(payload),
                "copy" => Copy(payload.GetProperty("text").GetString() ?? ""),
                "paste" => Clipboard.ContainsText() ? Clipboard.GetText() : "",
                _ when operation?.StartsWith("sftp", StringComparison.Ordinal) == true => await HandleSftpAsync(operation, payload.Clone()),
                _ => throw new InvalidOperationException("未知操作。")
            };
            Post(new { type = "reply", id = requestId, ok = true, result });
        }
        catch (Exception exception)
        {
            Post(new { type = "reply", id = requestId, ok = false, error = exception.Message });
        }
    }

    private object ControlWindow(string? action)
    {
        switch (action)
        {
            case "minimize": SystemCommands.MinimizeWindow(this); break;
            case "maximize":
                if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
                else SystemCommands.MaximizeWindow(this);
                break;
            case "close": Dispatcher.BeginInvoke(Close); break;
            case "state": break;
            default: throw new InvalidOperationException("未知窗口操作。");
        }
        return new { maximized = WindowState == WindowState.Maximized };
    }

    private Workspace SaveGroup(JsonElement payload)
    {
        var name = payload.GetProperty("name").GetString()?.Trim() ?? "";
        if (name.Length is < 1 or > 80) throw new InvalidDataException("请填写分组名称，最多 80 个字符。");
        if (workspace.Groups.Contains(name, StringComparer.Ordinal)) throw new InvalidDataException("该分组已存在，请使用其他名称。");
        workspace.Groups.Add(name);
        store.Save(workspace);
        return workspace;
    }

    private Workspace SaveHost(JsonElement payload)
    {
        var host = payload.Deserialize<HostProfile>(WorkspaceStore.JsonOptions) ?? throw new InvalidDataException("主机信息无效。");
        host.Name = host.Name.Trim();
        host.Address = host.Address.Trim().Trim('[', ']');
        host.Username = host.Username.Trim();
        host.Group = string.IsNullOrWhiteSpace(host.Group) ? "未分组" : host.Group.Trim();
        if (host.Name.Length is < 1 or > 100 || host.Address.Length is < 1 or > 255 || host.Username.Length is < 1 or > 100)
            throw new InvalidDataException("请填写主机名称、地址和用户名，并检查长度。");
        if (host.Address.Any(char.IsWhiteSpace) || host.Address.Contains('/') || host.Port is < 1 or > 65535)
            throw new InvalidDataException("主机地址或端口无效，请勿包含协议前缀或路径。");
        if (host.AuthType is not ("password" or "key")) throw new InvalidDataException("认证方式无效。");
        if (host.AuthType == "key" && !File.Exists(host.PrivateKeyPath)) throw new InvalidDataException("请选择有效的私钥文件。");
        if (host.Group.Length > 80 || host.Note.Length > 2000) throw new InvalidDataException("分组或备注过长。");
        var index = workspace.Hosts.FindIndex(item => item.Id == host.Id);
        if (index >= 0)
        {
            host.LastConnectedAt = workspace.Hosts[index].LastConnectedAt;
            workspace.Hosts[index] = host;
        }
        else
        {
            host.Id = Guid.NewGuid().ToString("N");
            workspace.Hosts.Add(host);
        }
        store.Save(workspace);
        return workspace;
    }

    private Workspace DeleteHost(string id)
    {
        workspace.Hosts.RemoveAll(host => host.Id == id);
        store.Save(workspace);
        return workspace;
    }

    private Workspace ToggleFavorite(string id)
    {
        var host = GetHost(id);
        host.Favorite = !host.Favorite;
        store.Save(workspace);
        return workspace;
    }

    private HostProfile GetHost(string id) => workspace.Hosts.Find(host => host.Id == id) ?? throw new InvalidDataException("主机不存在。");

    private Workspace SaveSnippet(JsonElement payload)
    {
        var snippet = payload.Deserialize<Snippet>(WorkspaceStore.JsonOptions) ?? throw new InvalidDataException("代码片段无效。");
        snippet.Name = snippet.Name.Trim();
        if (snippet.Name.Length is < 1 or > 100 || snippet.Command.Length is < 1 or > 20000)
            throw new InvalidDataException("请填写名称与命令，命令最多 20,000 字符。");
        var index = workspace.Snippets.FindIndex(item => item.Id == snippet.Id);
        if (index >= 0) workspace.Snippets[index] = snippet;
        else { snippet.Id = Guid.NewGuid().ToString("N"); workspace.Snippets.Add(snippet); }
        store.Save(workspace);
        return workspace;
    }

    private Workspace DeleteSnippet(string id)
    {
        workspace.Snippets.RemoveAll(snippet => snippet.Id == id);
        store.Save(workspace);
        return workspace;
    }

    private Workspace SavePreferences(JsonElement payload)
    {
        var preferences = payload.Deserialize<Preferences>(WorkspaceStore.JsonOptions) ?? new Preferences();
        preferences = PreferenceSettings.Normalize(preferences, strict: true);
        var previous = workspace.Preferences;
        workspace.Preferences = preferences;
        try { store.Save(workspace); }
        catch { workspace.Preferences = previous; throw; }
        Localization.Language = preferences.Language;
        Title = Localization.Text("TermLume · SSH 工作空间");
        return workspace;
    }

    private Workspace ForgetKey(string endpoint)
    {
        var entry = KnownHosts.Entries().Find(item => item.Endpoint == endpoint);
        if (entry is null || !KnownHosts.Forget(endpoint)) throw new InvalidDataException("信任记录不存在，请刷新列表。");
        Record("hostkey.removed", "已手动移除服务器信任，下次连接需要重新核验。", level: "warning", category: "security",
            details: new() { ["endpoint"] = entry.Endpoint, ["fingerprint"] = entry.Fingerprint }, server: entry);
        NotifyKnownHosts();
        return workspace;
    }

    private string? PickKey()
    {
        var dialog = new OpenFileDialog { Title = Localization.Text("选择 SSH 私钥"), Filter = Localization.Text("所有文件|*.*"), CheckFileExists = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private async Task<object> ConnectAsync(JsonElement payload)
    {
        var host = GetHost(payload.GetProperty("hostId").GetString()!);
        var sessionId = payload.GetProperty("sessionId").GetString()!;
        if (sessions.Count >= 12) throw new InvalidOperationException("最多同时打开 12 个连接，请先关闭不再使用的会话。");
        if (!Guid.TryParse(sessionId, out _) || sessions.ContainsKey(sessionId)) throw new InvalidDataException("会话标识无效。");
        Record("connection.started", "开始建立 SSH 连接。", host, sessionId);
        var dialog = new CredentialDialog(this, host.AuthType == "key" ? Localization.Text("解锁 SSH 私钥") : Localization.Text("连接到 ") + host.Name,
            $"{host.Username}@{host.Address}:{host.Port}\n" + (host.AuthType == "key" ? "请输入私钥口令；未加密的私钥可留空。" : "请输入 SSH 登录密码。"), host.AuthType == "key");
        if (dialog.ShowDialog() != true)
        {
            Record("connection.cancelled", "已取消凭据输入，未建立连接。", host, sessionId);
            throw new OperationCanceledException("已取消连接。");
        }
        SshSession? session = null;
        var connectionActivity = new SessionActivity(host);
        try
        {
            session = new SshSession(host, dialog.TakeSecret(), key => VerifyHostKey(host, key, sessionId));
            sessions.Add(sessionId, session);
            sessionActivity.Add(sessionId, connectionActivity);
            session.Connected += () => Dispatcher.Invoke(() =>
            {
                if (shuttingDown || !sessionActivity.TryGetValue(sessionId, out var activity)) return;
                activity.ConnectedAt = DateTimeOffset.UtcNow;
                Record("connection.connected", "SSH 连接成功，交互式终端已就绪。", host, sessionId);
                Post(new { type = "connected", sessionId });
            });
            session.Output += text => DispatchEvent(new { type = "output", sessionId, data = text });
            session.Ended += error => Dispatcher.BeginInvoke(() =>
            {
                if (!sessions.Remove(sessionId)) return;
                if (sessionActivity.TryGetValue(sessionId, out var activity) && activity.ConnectedAt is not null)
                    EndSessionActivity(sessionId, "connection.ended", error is null ? "远端已关闭 SSH 连接。" : "SSH 连接异常中断，请检查网络和服务器状态。", error is null ? "info" : "warning");
                Post(new { type = "ended", sessionId, error });
            });
            await session.ConnectAsync(Dimension(payload, "columns", 120), Dimension(payload, "rows", 30));
            if (shuttingDown || !sessions.ContainsKey(sessionId)) throw new OperationCanceledException("连接已取消。");
            host.LastConnectedAt = DateTimeOffset.Now;
            store.Save(workspace);
            return new { workspace };
        }
        catch (Exception exception)
        {
            sessions.Remove(sessionId);
            sessionActivity.Remove(sessionId);
            if (!shuttingDown && !connectionActivity.Closed)
                Record(exception is OperationCanceledException ? "connection.cancelled" : "connection.failed",
                    exception is OperationCanceledException ? "连接已取消。" : exception is SshAuthenticationException ? "SSH 认证失败，请检查用户名、密码或私钥。" : "连接未能完成，请检查网络、服务器、认证配置或本地文件权限。",
                    host, sessionId, exception is OperationCanceledException ? "info" : "error", details: new() { ["errorType"] = exception.GetType().Name });
            if (session is not null) await Task.Run(session.Dispose);
            throw;
        }
    }

    private bool VerifyHostKey(HostProfile host, HostKeyEventArgs key, string sessionId)
    {
        if (shuttingDown) return false;
        return Dispatcher.Invoke(() =>
        {
            if (shuttingDown) return false;
            var endpoint = KnownHostRegistry.Endpoint(host, key.HostKeyName);
            var fingerprint = "SHA256:" + key.FingerPrintSHA256;
            var status = KnownHosts.Verify(host, key.HostKeyName, fingerprint);
            if (status != HostKeyStatus.Unknown)
            {
                NotifyKnownHosts();
                if (status == HostKeyStatus.Trusted)
                {
                    Record("hostkey.verified", "服务器指纹与已保存的信任记录一致。", host, sessionId, category: "security", details: new() { ["endpoint"] = endpoint, ["fingerprint"] = fingerprint });
                    return true;
                }
                var known = workspace.KnownHosts[endpoint];
                Record("hostkey.changed", "服务器指纹发生变化，连接已阻止。请通过可信渠道核验。", host, sessionId, level: "error", category: "security",
                    details: new() { ["endpoint"] = endpoint, ["savedFingerprint"] = known, ["receivedFingerprint"] = fingerprint });
                MessageBox.Show(this, Localization.Text("{0} 的服务器指纹与保存的记录不一致。\n\n当前：{1}\n已保存：{2}\n\n连接已阻止。请联系管理员核验，不要盲目信任新指纹。", host.Address, fingerprint, known), Localization.Text("服务器身份发生变化"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            var answer = MessageBox.Show(this, Localization.Text("首次连接到 {0}:{1}\n\n算法：{2}\n指纹：{3}\n\n请通过可信渠道向管理员核对指纹。确认信任并保存吗？", host.Address, host.Port, key.HostKeyName, fingerprint), Localization.Text("确认 SSH 服务器身份"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                Record("hostkey.rejected", "用户未确认服务器指纹，连接已阻止。", host, sessionId, level: "warning", category: "security", details: new() { ["endpoint"] = endpoint, ["fingerprint"] = fingerprint });
                return false;
            }
            KnownHosts.Trust(host, key.HostKeyName, fingerprint);
            Record("hostkey.trusted", "用户确认并保存了首次连接的服务器指纹。", host, sessionId, category: "security", details: new() { ["endpoint"] = endpoint, ["fingerprint"] = fingerprint });
            NotifyKnownHosts();
            return true;
        });
    }

    private bool Disconnect(string sessionId)
    {
        if (sessions.Remove(sessionId, out var session))
        {
            EndSessionActivity(sessionId, "connection.disconnected", "用户主动断开 SSH 连接。");
            _ = Task.Run(session.Dispose);
        }
        return true;
    }

    private bool WriteInput(JsonElement payload)
    {
        if (!sessions.TryGetValue(payload.GetProperty("sessionId").GetString()!, out var session)) throw new InvalidOperationException("会话已断开。");
        if (!session.Write(payload.GetProperty("data").GetString() ?? "")) throw new InvalidOperationException("输入队列已满，请稍后重试。");
        return true;
    }

    private bool Resize(JsonElement payload)
    {
        if (sessions.TryGetValue(payload.GetProperty("sessionId").GetString()!, out var session)) session.Resize(Dimension(payload, "columns", 120), Dimension(payload, "rows", 30));
        return true;
    }

    private bool Acknowledge(JsonElement payload)
    {
        if (sessions.TryGetValue(payload.GetProperty("sessionId").GetString()!, out var session)) session.AcknowledgeOutput();
        return true;
    }

    private static uint Dimension(JsonElement payload, string name, uint fallback) => payload.TryGetProperty(name, out var dimension) && dimension.TryGetUInt32(out var value) ? Math.Clamp(value, 2, 1000) : fallback;
    private static bool Copy(string text) { Clipboard.SetText(text); return true; }
    private void DispatchEvent(object message)
    {
        if (!shuttingDown) Dispatcher.Invoke(() => Post(message));
    }
    private void Post(object message)
    {
        if (!shuttingDown && Browser.CoreWebView2 is not null) Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, WorkspaceStore.JsonOptions));
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if ((sessions.Count > 0 || sftpSessions.Count > 0 || transferJobs.Count > 0) && MessageBox.Show(this, Localization.Text("仍有 SSH / SFTP 连接或文件传输正在运行，确定取消传输、断开全部连接并退出吗？"), Localization.Text("退出 TermLume"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            args.Cancel = true;
            return;
        }
        foreach (var sessionId in sessions.Keys) EndSessionActivity(sessionId, "connection.disconnected", "应用退出，SSH 连接已关闭。");
        foreach (var sessionId in sftpSessions.Keys.ToArray()) DisconnectSftp(sessionId);
        Record("application.stopped", "TermLume 正常退出。", category: "application");
        shuttingDown = true;
        foreach (var session in sessions.Values) _ = Task.Run(session.Dispose);
        sessions.Clear();
        Browser.Dispose();
    }
}
