using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VPS.Tunnel.Service;

public sealed class SingBoxProcessRunner : IChildProcessRunner
{
    public bool HasConflictingSingBox()
    {
        // Discovery only. Never terminate an unowned instance.
        var processes = Process.GetProcessesByName("sing-box");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public IManagedChild Start(string configurationPath)
    {
        var console = IsolatedServiceConsole.Create();
        try
        {
            var info = new ProcessStartInfo(TunnelServiceLifecycle.ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = false, // A console is required for graceful Ctrl+Break.
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(TunnelServiceLifecycle.ExecutablePath)!
            };
            info.ArgumentList.Add("run");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(configurationPath);

            var process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null.");
            return new ManagedSingBoxChild(process, console);
        }
        catch
        {
            console.Dispose();
            throw;
        }
    }
}

internal sealed class ManagedSingBoxChild(Process process, IsolatedServiceConsole console) : IManagedChild
{
    // Drain without logging: sing-box diagnostics can contain sensitive data.
    private readonly Task _stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
    private readonly Task _stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
    public bool RequestGracefulStop() => console.SendCtrlBreak();
    public void ForceTerminate() => process.Kill(entireProcessTree: false);
    public void Dispose()
    {
        process.Dispose();
        console.Dispose();
        GC.KeepAlive(_stdout);
        GC.KeepAlive(_stderr);
    }
}

internal sealed class IsolatedServiceConsole : IDisposable
{
    private const uint CtrlBreakEvent = 1;
    private static readonly ConsoleControlHandler Handler = HandleControl;
    private bool _disposed;

    private IsolatedServiceConsole() { }

    public static IsolatedServiceConsole Create()
    {
        // The service is WinExe and SCM runs it in Session 0. A fresh console is
        // therefore dedicated to this service and its one managed child.
        if (GetConsoleWindow() != IntPtr.Zero)
            throw new InvalidOperationException("Service unexpectedly has a shared console.");
        if (!AllocConsole()) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetConsoleCtrlHandler(Handler, true))
        {
            var error = Marshal.GetLastWin32Error();
            FreeConsole();
            throw new Win32Exception(error);
        }
        return new IsolatedServiceConsole();
    }

    public bool SendCtrlBreak()
    {
        if (_disposed) return false;
        // Group 0 is safe here only because this newly allocated console contains
        // the service and its one child. The service handler consumes its own event.
        return GenerateConsoleCtrlEvent(CtrlBreakEvent, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SetConsoleCtrlHandler(Handler, false);
        FreeConsole();
    }

    private static bool HandleControl(uint controlType) => controlType == CtrlBreakEvent;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool ConsoleControlHandler(uint controlType);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(ConsoleControlHandler handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}

