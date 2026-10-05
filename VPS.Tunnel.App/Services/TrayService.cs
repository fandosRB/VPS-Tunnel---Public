using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VPS.Tunnel.Core;

namespace VPS.Tunnel.App.Services;

public sealed class TrayService : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 42;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint ImageIcon = 1, LrLoadFromFile = 0x10;
    private const uint WmRButtonUp = 0x0205, WmLButtonDoubleClick = 0x0203;
    private const uint MfString = 0, MfSeparator = 0x800;
    private const uint TpmReturnCmd = 0x100, TpmRightButton = 0x2;
    private readonly Window _window;
    private HwndSource? _source;
    private NotifyIconData _data;
    private bool _added;
    private IntPtr _loadedIcon;
    public bool IsAvailable => _added;
    public event EventHandler? OpenRequested;
    public event EventHandler? DirectRequested;
    public event EventHandler? TunnelRequested;
    public event EventHandler? SelectiveRequested;
    public event EventHandler? ExitRequested;

    public TrayService(Window window) => _window = window;

    public void Initialize()
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(WndProc);
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = new WindowInteropHelper(_window).Handle,
            Id = 1,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = CallbackMessage,
            Icon = LoadStateIcon(TrayIconState.Direct),
            Tip = "VPS Tunnel — проверка",
            Info = string.Empty,
            InfoTitle = string.Empty
        };
        _loadedIcon = _data.Icon;
        _added = Shell_NotifyIcon(NimAdd, ref _data);
    }

    public void SetState(ConnectionState state) => SetState(TrayIconState.FromConnectionState(state));

    public void SetState(TrayIconState state)
    {
        if (!_added) return;
        var nextIcon = LoadStateIcon(state);
        if (nextIcon != IntPtr.Zero)
        {
            var previousIcon = _loadedIcon;
            _loadedIcon = nextIcon;
            _data.Icon = nextIcon;
            if (previousIcon != IntPtr.Zero) _ = DestroyIcon(previousIcon);
        }
        _data.Tip = "VPS Tunnel — " + state.DisplayName;
        _ = Shell_NotifyIcon(NimModify, ref _data);
    }

    private IntPtr LoadStateIcon(TrayIconState state)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Icons", state.FileName);
        var icon = LoadImage(IntPtr.Zero, path, ImageIcon, 0, 0, LrLoadFromFile);
        return icon;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != CallbackMessage) return IntPtr.Zero;
        handled = true;
        if ((uint)lParam.ToInt64() == WmLButtonDoubleClick) OpenRequested?.Invoke(this, EventArgs.Empty);
        else if ((uint)lParam.ToInt64() == WmRButtonUp) ShowMenu();
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            _ = AppendMenu(menu, MfString, 1, "Открыть");
            _ = AppendMenu(menu, MfSeparator, 0, null);
            _ = AppendMenu(menu, MfString, 2, "DIRECT");
            _ = AppendMenu(menu, MfString, 5, "SELECTIVE");
            _ = AppendMenu(menu, MfString, 3, "TUNNEL");
            _ = AppendMenu(menu, MfSeparator, 0, null);
            _ = AppendMenu(menu, MfString, 4, "Выход");
            _ = GetCursorPos(out var point);
            _ = SetForegroundWindow(_data.Window);
            var selected = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton,
                point.X, point.Y, _data.Window, IntPtr.Zero);
            switch (selected)
            {
                case 1: OpenRequested?.Invoke(this, EventArgs.Empty); break;
                case 2: DirectRequested?.Invoke(this, EventArgs.Empty); break;
                case 3: TunnelRequested?.Invoke(this, EventArgs.Empty); break;
                case 4: ExitRequested?.Invoke(this, EventArgs.Empty); break;
                case 5: SelectiveRequested?.Invoke(this, EventArgs.Empty); break;
            }
        }
        finally { _ = DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_added) { _ = Shell_NotifyIcon(NimDelete, ref _data); _added = false; }
        if (_loadedIcon != IntPtr.Zero) { _ = DestroyIcon(_loadedIcon); _loadedIcon = IntPtr.Zero; }
        _source?.RemoveHook(WndProc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
