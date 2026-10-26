using System.Threading;
using System.Windows;
using System.IO;
using System.Text.Json;
using TermLume.Services;
using Localization = TermLume.Services.Localization;

namespace TermLume;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsMutex;

    protected override void OnStartup(StartupEventArgs args)
    {
        instanceMutex = new Mutex(true, "Local\\TermLume.Workspace", out ownsMutex);
        if (!ownsMutex)
        {
            try { Localization.Language = new WorkspaceStore().Load().Preferences.Language; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { Localization.Language = "zh-CN"; }
            MessageBox.Show(Localization.Text("TermLume 已在运行，请切换到现有窗口。"), "TermLume", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(args);
    }

    protected override void OnExit(ExitEventArgs args)
    {
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(args);
    }
}
