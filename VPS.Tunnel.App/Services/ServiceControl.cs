using System.ComponentModel;
using System.Runtime.InteropServices;
using VPS.Tunnel.Core;

namespace VPS.Tunnel.App.Services;

public enum ManagedServiceState { Missing, Stopped, StartPending, Running, StopPending, Other }
public sealed record ManagedServiceStatus(ManagedServiceState State, int ServicePid, int? SingBoxPid);

public interface IServiceControl
{
    ManagedServiceStatus Query();
    /// <summary>Starts the service; <paramref name="arguments"/> are passed to it by SCM.</summary>
    void Start(IReadOnlyList<string>? arguments = null);
    void Stop();
}

public interface IServiceModeStore
{
    /// <summary>Mode the service was last started in by this GUI (TUNNEL or SELECTIVE).</summary>
    TunnelMode ActiveServiceMode { get; }
    void SetActiveServiceMode(TunnelMode mode);
}

public sealed class InMemoryServiceModeStore : IServiceModeStore
{
    public TunnelMode ActiveServiceMode { get; private set; } = TunnelMode.Tunnel;
    public void SetActiveServiceMode(TunnelMode mode) => ActiveServiceMode = mode;
}

public interface IExitIpStore
{
    /// <summary>Public IP the tunnel should exit from, or null when not yet known.</summary>
    string? ExpectedExitIp { get; }
    /// <summary>Remembers the first exit IP seen through the tunnel when none is configured.</summary>
    void LearnExitIp(string ip);
}

public sealed class InMemoryExitIpStore(string? expected = null) : IExitIpStore
{
    public string? ExpectedExitIp { get; private set; } = expected;
    public void LearnExitIp(string ip) => ExpectedExitIp ??= ip;
}

public sealed class ServiceControlException(string code, string api, int windowsError)
    : Win32Exception(windowsError, $"{code} at {api} (Win32 {windowsError})")
{
    public string DiagnosticCode { get; } = code;
    public string Api { get; } = api;
}

public sealed class WindowsServiceControl : IServiceControl
{
    private const string Name = "VpsTunnelService";
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004, ServiceStart = 0x0010, ServiceStop = 0x0020;
    private int? _lastChildPid;

    public ManagedServiceStatus Query()
    {
        using var service = Open(ServiceQueryStatus, allowMissing: true);
        if (service.IsInvalid) return new(ManagedServiceState.Missing, 0, null);
        if (!QueryServiceStatusEx(service.Handle, 0, out var status,
                Marshal.SizeOf<ServiceStatusProcess>(), out _))
            throw Failure("SERVICE_QUERY_FAILED", "QueryServiceStatusEx");
        var state = status.CurrentState switch
        {
            1 => ManagedServiceState.Stopped,
            2 => ManagedServiceState.StartPending,
            3 => ManagedServiceState.StopPending,
            4 => ManagedServiceState.Running,
            _ => ManagedServiceState.Other
        };
        if (state == ManagedServiceState.Running)
            _lastChildPid = FindChildSingBox((int)status.ProcessId);
        else if (_lastChildPid is int pid && !IsSingBoxAlive(pid)) _lastChildPid = null;
        return new(state, (int)status.ProcessId, _lastChildPid);
    }

    public void Start(IReadOnlyList<string>? arguments = null)
    {
        using var service = Open(ServiceStart | ServiceQueryStatus);
        var argv = arguments?.ToArray() ?? [];
        if (!StartService(service.Handle, (uint)argv.Length, argv.Length == 0 ? null : argv))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1056) throw new ServiceControlException("SERVICE_START_FAILED", "StartServiceW", error);
        }
    }

    public void Stop()
    {
        using var service = Open(ServiceStop | ServiceQueryStatus);
        if (!ControlService(service.Handle, 1, out _))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1062) throw new ServiceControlException("SERVICE_STOP_FAILED", "ControlService", error);
        }
    }

    private static ServiceHandle Open(uint access, bool allowMissing = false)
    {
        using var scm = new ServiceHandle(OpenSCManager(null, null, ScManagerConnect));
        if (scm.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new ServiceControlException(error == 5 ? "SCM_ACCESS_DENIED" : "SCM_OPEN_FAILED",
                "OpenSCManagerW", error);
        }
        var handle = OpenService(scm.Handle, Name, access);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (!allowMissing || error != 1060)
                throw new ServiceControlException(error switch
                {
                    5 => "SERVICE_ACCESS_DENIED",
                    1060 => "SERVICE_NOT_INSTALLED",
                    _ => "SERVICE_OPEN_FAILED"
                }, "OpenServiceW", error);
        }
        return new ServiceHandle(handle);
    }

    private static int? FindChildSingBox(int parentPid)
    {
        if (parentPid <= 0) return null;
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new IntPtr(-1)) throw Failure("PROCESS_ENUM_FAILED", "CreateToolhelp32Snapshot");
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) return null;
            do
            {
                if (entry.ParentProcessId != (uint)parentPid ||
                    !string.Equals(entry.ExeFile, "sing-box.exe", StringComparison.OrdinalIgnoreCase)) continue;
                // The process entry itself is sufficient evidence. Opening a
                // LocalSystem child handle here raises ERROR_ACCESS_DENIED.
                return (int)entry.ProcessId;
            } while (Process32Next(snapshot, ref entry));
            return null;
        }
        finally { CloseHandle(snapshot); }
    }

    private static bool IsSingBoxAlive(int pid)
    {
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new IntPtr(-1)) throw Failure("PROCESS_ENUM_FAILED", "CreateToolhelp32Snapshot");
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) return false;
            do
            {
                if (entry.ProcessId == (uint)pid &&
                    string.Equals(entry.ExeFile, "sing-box.exe", StringComparison.OrdinalIgnoreCase)) return true;
            } while (Process32Next(snapshot, ref entry));
            return false;
        }
        finally { CloseHandle(snapshot); }
    }

    private static ServiceControlException Failure(string code, string api) =>
        new(code, api, Marshal.GetLastWin32Error());

    private sealed class ServiceHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;
        public bool IsInvalid => Handle == IntPtr.Zero;
        public void Dispose() { if (!IsInvalid) CloseServiceHandle(Handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatusProcess status,
        int bufferSize, out int bytesNeeded);
    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartService(IntPtr service, uint count,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[]? args);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr service);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
