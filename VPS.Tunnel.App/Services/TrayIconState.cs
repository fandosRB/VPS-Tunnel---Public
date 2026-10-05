using VPS.Tunnel.Core;

namespace VPS.Tunnel.App.Services;

public sealed record TrayIconState(string FileName, string DisplayName)
{
    public static readonly TrayIconState Direct = new("tray-direct.ico", "DIRECT");
    public static readonly TrayIconState Tunnel = new("tray-tunnel.ico", "TUNNEL");
    public static readonly TrayIconState Selective = new("tray-tunnel.ico", "SELECTIVE");
    public static readonly TrayIconState Connecting = new("tray-connecting.ico", "ПОДКЛЮЧЕНИЕ");
    public static readonly TrayIconState Error = new("tray-error.ico", "ОШИБКА");

    public static TrayIconState FromConnectionState(ConnectionState state) => state switch
    {
        ConnectionState.Tunnel or ConnectionState.TunnelActiveRouteConflict => Tunnel,
        ConnectionState.Selective => Selective,
        ConnectionState.Connecting or ConnectionState.Disconnecting => Connecting,
        ConnectionState.Error => Error,
        _ => Direct
    };
}
