using VPS.Tunnel.Core;

namespace VPS.Tunnel.Service;

public interface IFileProbe
{
    bool Exists(string path);
}

public interface IServiceEventLog
{
    void Write(string eventName, int? pid = null, Exception? exception = null);
}

public interface IChildProcessRunner
{
    bool HasConflictingSingBox();
    IManagedChild Start(string configurationPath);
}

/// <summary>Arguments SCM passed to StartService. Empty for a plain start (full TUNNEL).</summary>
public sealed class ServiceStartArguments
{
    private volatile string[] _values = [];
    public IReadOnlyList<string> Values => _values;
    public void Set(string[]? values) => _values = values ?? [];
}

public interface ISelectiveConfigurationWriter
{
    /// <summary>Writes the SELECTIVE configuration derived from the base one and returns its path.</summary>
    string Write(SelectiveRequest request);
}

public interface IManagedChild : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    Task WaitForExitAsync(CancellationToken token);
    bool RequestGracefulStop();
    void ForceTerminate();
}

public sealed class PhysicalFileProbe : IFileProbe
{
    public bool Exists(string path) => File.Exists(path);
}

public sealed class TunnelServiceLifecycle
{
    public const string ExecutablePath = @"C:\sing-box\sing-box-1.14.1-windows-amd64\sing-box.exe";
    public const string ConfigurationPath = @"C:\sing-box\config.json";

    private readonly IFileProbe _files;
    private readonly IChildProcessRunner _runner;
    private readonly IServiceEventLog _log;
    private readonly Action _stopApplication;
    private readonly ServiceStartArguments _arguments;
    private readonly ISelectiveConfigurationWriter? _selective;
    private readonly TimeSpan _graceTimeout;
    private readonly TimeSpan _forcedTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IManagedChild? _child;
    private Task? _monitorTask;
    private volatile bool _stopping;
    private bool _started;
    private bool _stopped;

    public TunnelServiceLifecycle(IFileProbe files, IChildProcessRunner runner, IServiceEventLog log,
        Action stopApplication, TimeSpan? graceTimeout = null, TimeSpan? forcedTimeout = null,
        ServiceStartArguments? arguments = null, ISelectiveConfigurationWriter? selective = null)
    {
        _files = files;
        _runner = runner;
        _log = log;
        _stopApplication = stopApplication;
        _arguments = arguments ?? new ServiceStartArguments();
        _selective = selective;
        _graceTimeout = graceTimeout ?? TimeSpan.FromSeconds(20);
        _forcedTimeout = forcedTimeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task StartAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (_started) throw new InvalidOperationException("Service already started.");
            _log.Write("ServiceStarting");
            if (!_files.Exists(ExecutablePath)) throw new FileNotFoundException("Sing-box executable missing.");
            if (!_files.Exists(ConfigurationPath)) throw new FileNotFoundException("Sing-box config missing.");
            if (_runner.HasConflictingSingBox())
            {
                _log.Write("SingBoxConflict");
                throw new InvalidOperationException("Another sing-box is already running.");
            }

            var configuration = ConfigurationPath;
            var request = SelectiveApplications.ParseStartArguments(_arguments.Values);
            if (request != null)
            {
                _log.Write(request.IncludeWsl ? "SelectiveModeRequested WSL" : "SelectiveModeRequested");
                if (_selective == null) throw new InvalidOperationException("Selective mode is unavailable.");
                configuration = _selective.Write(request);
                _log.Write("SelectiveConfigurationWritten");
            }

            _log.Write("SingBoxStarting");
            var child = _runner.Start(configuration);
            _child = child;
            _started = true;
            _log.Write("SingBoxStarted", child.Id);
            _monitorTask = MonitorAsync(child);
        }
        catch (Exception ex)
        {
            _log.Write("Exception", exception: ex);
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task MonitorAsync(IManagedChild child)
    {
        try
        {
            await child.WaitForExitAsync(CancellationToken.None);
            _log.Write("SingBoxExited", child.Id);
            if (!_stopping) _stopApplication();
        }
        catch (Exception ex)
        {
            _log.Write("Exception", child.Id, ex);
            if (!_stopping) _stopApplication();
        }
    }

    public async Task StopAsync(CancellationToken token)
    {
        // SCM's token can expire, but cleanup must still be attempted.
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_stopped) return;
            _stopping = true;
            _log.Write("ServiceStopping");
            if (_child is { } child)
            {
                if (!child.HasExited)
                {
                    _log.Write("GracefulStopRequested", child.Id);
                    var signalled = child.RequestGracefulStop();
                    if (signalled && await WaitBoundedAsync(child, _graceTimeout))
                        _log.Write("GracefulStopSucceeded", child.Id);
                    else if (!child.HasExited)
                    {
                        _log.Write("FORCED_TERMINATION", child.Id);
                        child.ForceTerminate();
                        if (!await WaitBoundedAsync(child, _forcedTimeout))
                            throw new TimeoutException("Managed sing-box did not exit after forced fallback.");
                    }
                }
                if (_monitorTask != null) await _monitorTask;
                child.Dispose();
                _child = null;
                _monitorTask = null;
            }
            _stopped = true;
            _log.Write("ServiceStopped");
        }
        catch (Exception ex)
        {
            _log.Write("Exception", _child?.Id, ex);
            throw;
        }
        finally { _gate.Release(); }
    }

    private static async Task<bool> WaitBoundedAsync(IManagedChild child, TimeSpan timeout)
    {
        if (child.HasExited) return true;
        using var deadline = new CancellationTokenSource(timeout);
        try { await child.WaitForExitAsync(deadline.Token); return true; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return child.HasExited; }
    }
}
