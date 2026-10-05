using System.Security.Principal;
using System.Windows;
using VPS.Tunnel.App.Services;

namespace VPS.Tunnel.App;

public partial class App : Application
{
    private SingleInstanceService? _singleInstance;
    public bool IsAutomaticLaunch { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == ConnectionConfigWriter.Argument)
        {
            // Elevated helper instance: install the connection configuration and exit without UI.
            Shutdown(ConnectionConfigWriter.RunElevated(e.Args[1]));
            return;
        }
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _singleInstance = new SingleInstanceService(sid);
        if (!_singleInstance.TryAcquire())
        {
            _ = _singleInstance.ActivateExistingAsync().GetAwaiter().GetResult();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }
        IsAutomaticLaunch = e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
        _singleInstance.ActivationRequested += (_, _) => Dispatcher.BeginInvoke(() =>
            (MainWindow as VPS.Tunnel.App.MainWindow)?.RestoreFromExternalLaunch());
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        WindowStartupPresenter.Present(window);
        _ = window.InitializeStartupAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
