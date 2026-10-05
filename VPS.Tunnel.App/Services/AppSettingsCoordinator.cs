using VPS.Tunnel.Core;

namespace VPS.Tunnel.App.Services;

public sealed class AppSettingsCoordinator : IServiceModeStore, IExitIpStore
{
    private readonly AppSettingsService _store;
    private readonly WindowsStartupService _startup;
    public AppSettings Settings { get; }
    public bool IsInitializing { get; private set; } = true;

    public AppSettingsCoordinator(AppSettingsService store, WindowsStartupService startup)
    {
        _store = store;
        _startup = startup;
        Settings = store.Load();
    }

    public void FinishInitialization() => IsInitializing = false;

    public void SetStartWithWindows(bool enabled)
    {
        if (IsInitializing) return;
        _startup.Apply(enabled);
        Settings.LaunchWithWindows = enabled;
        _store.Save(Settings);
    }

    public void SetStartMinimized(bool enabled)
    {
        if (IsInitializing) return;
        Settings.LaunchMinimized = enabled;
        _store.Save(Settings);
    }

    public void SetStartupMode(StartupMode mode)
    {
        if (IsInitializing) return;
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Settings.StartupMode = mode;
        _store.Save(Settings);
    }

    public void SetTheme(ThemePreference theme)
    {
        if (IsInitializing) return;
        Settings.Theme = theme;
        _store.Save(Settings);
    }

    public void RememberMode(TunnelMode mode)
    {
        if (IsInitializing) return;
        if (!Enum.IsDefined(mode)) return;
        Settings.LastSelectedMode = mode;
        _store.Save(Settings);
    }

    public SelectiveRequest SelectiveRequest =>
        new(Settings.SelectiveApplications, Settings.SelectiveIncludeWsl);

    public void SetSelective(IEnumerable<string> applications, bool includeWsl)
    {
        Settings.SelectiveApplications = SelectiveApplications.Normalize(applications);
        Settings.SelectiveIncludeWsl = includeWsl;
        _store.Save(Settings);
    }

    public string? ExpectedExitIp => Settings.ExpectedExitIp;

    public void LearnExitIp(string ip)
    {
        if (Settings.ExpectedExitIp != null) return;
        Settings.ExpectedExitIp = ip;
        _store.Save(Settings);
    }

    /// <summary>Records a newly written connection; the exit IP is the server IP when known, else learned later.</summary>
    public void SetConnection(string displayName, string? expectedExitIp, bool configured)
    {
        Settings.ServerDisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (configured)
        {
            Settings.ExpectedExitIp = expectedExitIp;
            Settings.ConnectionConfigured = true;
        }
        _store.Save(Settings);
    }

    public TunnelMode ActiveServiceMode => Settings.ActiveServiceMode;

    public void SetActiveServiceMode(TunnelMode mode)
    {
        if (mode is not (TunnelMode.Tunnel or TunnelMode.Selective)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (Settings.ActiveServiceMode == mode) return;
        Settings.ActiveServiceMode = mode;
        _store.Save(Settings);
    }
}
