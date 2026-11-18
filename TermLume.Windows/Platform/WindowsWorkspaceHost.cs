using System.Text.Json;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using TermLume.Shared.Abstractions;
using TermLume.Shared.Services;
using TermLume.Windows.Views;
using Localization = TermLume.Shared.Services.Localization;

namespace TermLume.Windows.Platform;

internal sealed class WindowsWorkspaceHost(Window owner, Action<object> send) : IWorkspaceHost
{
    public void Send(object message) => send(message);

    public Task<T?> RequestAsync<T>(string operation, object? payload = null)
    {
        owner.Dispatcher.VerifyAccess();
        var request = JsonSerializer.SerializeToElement(payload, WorkspaceStore.JsonOptions);
        object? result = operation switch
        {
            "credential" => RequestSecret(request),
            "trustHost" => MessageBox.Show(owner, request.GetProperty("message").GetString(), request.GetProperty("title").GetString(),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes,
            "securityNotice" => ShowSecurityNotice(request),
            "pickFolder" => PickFolder(request),
            "exportLogs" => PickLogExport(request),
            "openBrowser" => OpenBrowser(request),
            "copy" => Copy(request.GetProperty("text").GetString()!),
            "paste" => Clipboard.ContainsText() ? Clipboard.GetText() : "",
            "pickKey" => PickKey(),
            _ => throw new InvalidOperationException("未知原生操作。")
        };
        return Task.FromResult(result is null ? default : (T?)result);
    }

    private static bool Copy(string text) { Clipboard.SetText(text); return true; }

    private string? PickKey()
    {
        var dialog = new OpenFileDialog { Title = Localization.Text("选择 SSH 私钥"), Filter = Localization.Text("所有文件|*.*"), CheckFileExists = true };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private static bool OpenBrowser(JsonElement request)
    {
        var raw = request.GetProperty("url").GetString() ?? "";
        var uri = new Uri(raw, UriKind.Absolute);
        var issuer = new Uri(AegisOptions.Load().Issuer);
        if (uri.GetLeftPart(UriPartial.Authority) != issuer.GetLeftPart(UriPartial.Authority) ||
            uri.AbsolutePath != "/oauth/authorize" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("登录地址无效。");
        Process.Start(new ProcessStartInfo(raw) { UseShellExecute = true });
        return true;
    }

    private string? RequestSecret(JsonElement request)
    {
        var key = request.GetProperty("key").GetBoolean();
        var dialog = new CredentialDialog(owner, request.GetProperty("title").GetString()!,
            request.GetProperty("endpoint").GetString() + "\n" + (key ? "请输入私钥口令；未加密的私钥可留空。" : "请输入 SSH 登录密码。"), key);
        return dialog.ShowDialog() == true ? dialog.TakeSecret() : null;
    }

    private bool ShowSecurityNotice(JsonElement request)
    {
        MessageBox.Show(owner, request.GetProperty("message").GetString(), request.GetProperty("title").GetString(), MessageBoxButton.OK, MessageBoxImage.Error);
        return true;
    }

    private string? PickFolder(JsonElement request)
    {
        var dialog = new OpenFolderDialog { Title = request.GetProperty("title").GetString()!, Multiselect = false };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }

    private string? PickLogExport(JsonElement request)
    {
        var dialog = new SaveFileDialog
        {
            Title = request.GetProperty("title").GetString()!, Filter = Localization.Text("JSON 日志|*.json"),
            DefaultExt = ".json", AddExtension = true, FileName = request.GetProperty("name").GetString()!, OverwritePrompt = true
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}
