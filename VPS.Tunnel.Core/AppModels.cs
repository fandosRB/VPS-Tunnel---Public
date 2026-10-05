namespace VPS.Tunnel.Core;

public enum TunnelMode { Direct, Selective, Tunnel }
public enum ConnectionState { Unknown, Direct, Connecting, Tunnel, TunnelActiveRouteConflict, Disconnecting, Error, Selective }
public enum StartupMode { Direct, LastMode, Tunnel, Selective }
public enum ThemePreference { System, Light, Dark }

public sealed class AppSettings
{
    public bool LaunchWithWindows { get; set; }
    public bool LaunchMinimized { get; set; }
    public StartupMode StartupMode { get; set; } = StartupMode.Direct;
    public TunnelMode LastSelectedMode { get; set; } = TunnelMode.Direct;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public List<string> SelectiveApplications { get; set; } = [];
    public bool SelectiveIncludeWsl { get; set; }
    /// <summary>Server name shown in the GUI. Secrets live only in C:\sing-box\config.json.</summary>
    public string? ServerDisplayName { get; set; }
    /// <summary>Public IP the tunnel is expected to exit from; null until known.</summary>
    public string? ExpectedExitIp { get; set; }
    /// <summary>Set once this GUI has written a connection configuration.</summary>
    public bool ConnectionConfigured { get; set; }
    /// <summary>Mode the GUI last started the service in; tells TUNNEL and SELECTIVE apart on restart.</summary>
    public TunnelMode ActiveServiceMode { get; set; } = TunnelMode.Tunnel;
}

public static class StartupBehavior
{
    public static TunnelMode TargetMode(AppSettings settings) => settings.StartupMode switch
    {
        StartupMode.Tunnel => TunnelMode.Tunnel,
        StartupMode.Selective => TunnelMode.Selective,
        StartupMode.LastMode => settings.LastSelectedMode,
        _ => TunnelMode.Direct
    };

    public static bool ShouldConnect(AppSettings settings) => TargetMode(settings) != TunnelMode.Direct;

    public static bool ShouldInitiateTunnel(AppSettings settings, ConnectionState detectedState) =>
        ShouldConnect(settings) && detectedState == ConnectionState.Direct;

    public static bool ShouldHideAtLaunch(AppSettings settings, bool automaticLaunch) =>
        automaticLaunch && settings.LaunchMinimized;

    public static bool ShouldRememberSelection(TunnelMode selected, ConnectionState result) =>
        selected == TunnelMode.Direct && result == ConnectionState.Direct ||
        selected == TunnelMode.Tunnel && result == ConnectionState.Tunnel ||
        selected == TunnelMode.Selective && result == ConnectionState.Selective;
}

public static class TrayBehavior
{
    public static bool HideOnClose(bool exitRequested, bool trayAvailable) => !exitRequested && trayAvailable;
}

public sealed record TrafficCounters(long ReceivedBytes, long SentBytes);
public sealed record ConnectionSnapshot(
    ConnectionState State,
    string? PublicIPv4,
    string? Country,
    string? Error,
    DateTimeOffset? ConnectedAt,
    TrafficCounters? Traffic,
    int? SingBoxPid);

public interface INetworkStatusService
{
    bool HasLikelyTunInterface();
    bool HasActiveTunRoute();
    TrafficCounters? ReadReliableTunTraffic();
}

public interface IConnectionInfoService
{
    Task<string?> GetPublicIPv4Async(bool forceRefresh, CancellationToken cancellationToken);
    Task<string?> GetCountryAsync(string ipv4, CancellationToken cancellationToken);
}

public interface ISessionStatisticsService
{
    void Begin(DateTimeOffset started, TrafficCounters? baseline);
    void Reset();
    (TimeSpan Elapsed, TrafficCounters? Delta) Read(TrafficCounters? current);
}

public interface IAppSettingsService
{
    AppSettings Load();
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}
