using System.Globalization;
using System.Windows;

namespace TermLume.Windows.Views;

public sealed partial class ExitDialog : Window
{
    public ExitDialog(Window owner, int sshConnections, int sftpConnections, int transfers)
    {
        InitializeComponent();
        Owner = owner;
        Icon = owner.Icon;
        SshCount.Text = sshConnections.ToString(CultureInfo.InvariantCulture);
        SftpCount.Text = sftpConnections.ToString(CultureInfo.InvariantCulture);
        TransferCount.Text = transfers.ToString(CultureInfo.InvariantCulture);
        Loaded += (_, _) => KeepWorking.Focus();
    }

    private void ConfirmExit(object sender, RoutedEventArgs args) => DialogResult = true;
}
