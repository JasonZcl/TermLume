using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using TermLume.Shared.Application;
using TermLume.Windows.Platform;
using TermLume.Shared.Services;
using TermLume.Windows.Views;
using Localization = TermLume.Shared.Services.Localization;

namespace TermLume.Windows;

public partial class MainWindow : Window
{
    private const string AppOrigin = "https://termlume.local";
    private WorkspaceController? controller;
    private bool shuttingDown;
    private bool exitPromptOpen;
    private bool closeApproved;

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
            controller = new WorkspaceController(WorkspaceStore.DataDirectory,
                new WindowsWorkspaceHost(this, Post), new WindowsWorkspaceDispatcher(Dispatcher));
            Title = Localization.Text("TermLume · SSH 工作空间");
            StartupMessage.Text = Localization.Text("正在准备你的工作空间…");
            StartupClose.Content = Localization.Text("关闭");
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(WorkspaceStore.DataDirectory, "WebView2"));
            if (shuttingDown) return;
            await Browser.EnsureCoreWebView2Async(environment);
            if (shuttingDown) return;
            var core = Browser.CoreWebView2;
            // 虚拟域名仅映射安装目录内的界面资源；后续导航、弹窗、权限和浏览器下载均受限制。
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
        if (args.Source != AppOrigin + "/index.html" || shuttingDown || controller is null) return;
        string? requestId = null;
        try
        {
            if (args.WebMessageAsJson.Length > 2 * 1024 * 1024) throw new InvalidDataException("请求数据过大。");
            using var document = JsonDocument.Parse(args.WebMessageAsJson, new JsonDocumentOptions { MaxDepth = 32 });
            var request = document.RootElement;
            requestId = request.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(requestId) || requestId.Length > 100) throw new InvalidDataException("请求标识无效。");
            var operation = request.GetProperty("op").GetString();
            var payload = request.GetProperty("payload");
            if (operation is "window" or "pickKey" or "copy" or "paste")
            {
                object? result = operation switch
                {
                    "window" => ControlWindow(payload.GetProperty("action").GetString()),
                    "pickKey" => PickKey(),
                    "copy" => Copy(payload.GetProperty("text").GetString() ?? ""),
                    _ => Clipboard.ContainsText() ? Clipboard.GetText() : ""
                };
                Post(new { type = "reply", id = requestId, ok = true, result });
                return;
            }
            if (operation is "status" or "shutdown") throw new InvalidOperationException("未知操作。");
            var paths = operation == "sftpDropUpload" ? GetDroppedPaths(args.AdditionalObjects) : null;
            await controller.HandleAsync(request, paths);
            Title = Localization.Text("TermLume · SSH 工作空间");
        }
        catch (Exception exception)
        {
            Post(new { type = "reply", id = requestId, ok = false, error = exception.Message });
        }
    }

    private static string[] GetDroppedPaths(IReadOnlyList<object> additionalObjects) => additionalObjects.Select(item =>
        item is CoreWebView2File file ? file.Path : throw new InvalidDataException("只能拖入资源管理器中的普通文件。")).ToArray();

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

    private string? PickKey()
    {
        var dialog = new OpenFileDialog { Title = Localization.Text("选择 SSH 私钥"), Filter = Localization.Text("所有文件|*.*"), CheckFileExists = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private static bool Copy(string text) { Clipboard.SetText(text); return true; }

    private void Post(object message)
    {
        if (!shuttingDown && Browser.CoreWebView2 is not null) Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, WorkspaceStore.JsonOptions));
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (closeApproved) return;
        args.Cancel = true;
        if (exitPromptOpen || shuttingDown) return;
        exitPromptOpen = true;
        try
        {
            var status = controller?.Status;
            if (status is not null && (status.Ssh > 0 || status.Sftp > 0 || status.Transfers > 0) &&
                new ExitDialog(this, status.Ssh, status.Sftp, status.Transfers).ShowDialog() != true) return;
            shuttingDown = true;
            try
            {
                if (controller is not null) await controller.ShutdownAsync();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, Localization.Text(exception.Message), "TermLume", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                Browser.Dispose();
                closeApproved = true;
                _ = Dispatcher.BeginInvoke(Close);
            }
        }
        finally { exitPromptOpen = false; }
    }
}
