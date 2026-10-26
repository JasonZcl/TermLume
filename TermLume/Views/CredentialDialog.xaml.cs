using System.Windows;
using System.Windows.Input;
using TermLume.Services;
using Localization = TermLume.Services.Localization;

namespace TermLume.Views;

public sealed partial class CredentialDialog : Window
{
    public string Secret { get; private set; } = "";

    /// <summary>向本次连接交付凭据并清空保存引用；托管字符串不保证即时安全擦除。</summary>
    public string TakeSecret()
    {
        var value = Secret;
        Secret = "";
        return value;
    }

    public CredentialDialog(Window owner, string title, string description, bool privateKey = false)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        Icon = owner.Icon;
        Heading.Text = title;
        var lines = description.Split('\n', 2);
        Endpoint.Text = lines[0];
        SecretLabel.Text = Localization.Text(privateKey ? "私钥解锁口令" : "SSH 登录密码");
        InputHint.Text = Localization.Text(lines.Length > 1 ? lines[1] : "输入本次连接使用的密码。");
        ConnectButton.Click += (_, _) => { Secret = SecretInput.Password; DialogResult = true; };
        Loaded += (_, _) => { SecretInput.Focus(); UpdateCapsLock(); };
        Activated += (_, _) => UpdateCapsLock();
        PreviewKeyDown += (_, _) => UpdateCapsLock();
        PreviewKeyUp += (_, _) => UpdateCapsLock();
        // 无论确认还是取消，都清空输入控件；取消时不向调用方保留任何凭据。
        Closed += (_, _) => { SecretInput.Clear(); if (DialogResult != true) Secret = ""; };
    }

    private void UpdateCapsLock() => CapsLockHint.Visibility = Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;
}
