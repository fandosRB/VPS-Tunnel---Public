using System.ComponentModel;
using VPS.Tunnel.Core;

namespace VPS.Tunnel.App.Services;

public sealed class ServiceConnectionCoordinator
{
    private const string SetupRequired = "VPS Tunnel требует завершить первоначальную настройку.";
    private readonly IServiceControl _service;
    private readonly IConnectionInfoService _connection;
    private readonly INetworkStatusService _network;
    private readonly ISessionStatisticsService _session;
    private readonly SafeLog _log;
    private readonly IServiceModeStore _modes;
    private readonly IExitIpStore _exitIps;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _poll;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private long _networkGeneration;
    private ConnectionSnapshot _last = new(ConnectionState.Unknown, null, null, null, null, null, null);
    public bool ServiceAvailable { get; private set; } = true;

    public ServiceConnectionCoordinator(IServiceControl service, IConnectionInfoService connection,
        INetworkStatusService network, ISessionStatisticsService session, SafeLog log,
        TimeSpan? timeout = null, TimeSpan? poll = null, IServiceModeStore? modes = null,
        IExitIpStore? exitIps = null)
    {
        _service = service; _connection = connection; _network = network; _session = session; _log = log;
        _modes = modes ?? new InMemoryServiceModeStore();
        _exitIps = exitIps ?? new InMemoryExitIpStore();
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _poll = poll ?? TimeSpan.FromMilliseconds(400);
    }

    public async Task<ConnectionSnapshot> DetectStartupAsync(CancellationToken token)
    {
        await _operation.WaitAsync(token);
        try
        {
            var status = _service.Query();
            ServiceAvailable = status.State != ManagedServiceState.Missing;
            if (!ServiceAvailable) return Error("SERVICE_NOT_INSTALLED", SetupRequired);
            if (status.State == ManagedServiceState.Running)
            {
                if (status.SingBoxPid != null && _network.HasLikelyTunInterface() && _network.HasActiveTunRoute())
                    return (await ActiveTunnelAsync(status.SingBoxPid, token))!;
                return Error("TUN_NOT_CONFIRMED", "Служба запущена, но процесс или TUN ещё не подтверждён.");
            }
            if (status.State == ManagedServiceState.Stopped && !_network.HasLikelyTunInterface() &&
                !_network.HasActiveTunRoute()) return await DirectAsync(token);
            return Error("STATE_NOT_CONFIRMED", "Не удалось подтвердить состояние службы и TUN.");
        }
        catch (ServiceControlException ex) { return ControlError(ex); }
        catch (Win32Exception ex) { return Error("WINDOWS_API_ERROR", "Не удалось проверить службу Windows.", ex.NativeErrorCode); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _log.Exception("startup detection", ex); return Error("STARTUP_CHECK_FAILED", "Не удалось определить состояние подключения."); }
        finally { _operation.Release(); }
    }

    public Task<ConnectionSnapshot> ConnectAsync(CancellationToken token) =>
        ConnectAsync(TunnelMode.Tunnel, null, restart: false, token);

    /// <summary>
    /// Brings the service up in TUNNEL or SELECTIVE. A running service in the other mode, or
    /// with <paramref name="restart"/> set (changed SELECTIVE list), is stopped first.
    /// </summary>
    public async Task<ConnectionSnapshot> ConnectAsync(TunnelMode mode, SelectiveRequest? selective,
        bool restart, CancellationToken token)
    {
        if (mode is not (TunnelMode.Tunnel or TunnelMode.Selective)) throw new ArgumentOutOfRangeException(nameof(mode));
        var selected = new SelectiveRequest(SelectiveApplications.Normalize(selective?.Applications),
            selective?.IncludeWsl == true);
        if (mode == TunnelMode.Selective && selected.IsEmpty)
            return Error("SELECTIVE_EMPTY", "Добавьте программу или включите WSL для режима SELECTIVE.");
        Interlocked.Increment(ref _networkGeneration);
        await _operation.WaitAsync(token);
        var serviceSeen = false;
        var processSeen = false;
        var tunSeen = false;
        try
        {
            _log.Write(mode == TunnelMode.Selective ? "mode requested: selective" : "mode requested: tunnel");
            var status = _service.Query();
            if (status.State == ManagedServiceState.Missing) { ServiceAvailable = false; return Error("SERVICE_NOT_INSTALLED", SetupRequired); }
            if (status.State != ManagedServiceState.Stopped && (restart || _modes.ActiveServiceMode != mode))
            {
                _log.Write("service restart requested");
                _session.Reset();
                _last = _last with { State = ConnectionState.Disconnecting, ConnectedAt = null };
                if (status.State == ManagedServiceState.Running) _service.Stop();
                if (!await WaitForStoppedAsync(token))
                    return Error("SERVICE_RESTART_TIMEOUT", "Не удалось перезапустить туннель за 30 секунд.");
                status = _service.Query();
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_timeout);
            if (status.State == ManagedServiceState.Stopped)
            {
                _modes.SetActiveServiceMode(mode);
                _service.Start(mode == TunnelMode.Selective ? SelectiveApplications.ToStartArguments(selected) : null);
                _log.Write("service start requested");
            }
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                status = _service.Query();
                if (status.State == ManagedServiceState.Stopped)
                    return Error("SERVICE_STOPPED_DURING_START", "Служба завершилась до создания туннеля.");
                serviceSeen |= status.State == ManagedServiceState.Running;
                processSeen |= status.SingBoxPid != null;
                var tun = status.State == ManagedServiceState.Running && status.SingBoxPid != null &&
                    _network.HasLikelyTunInterface();
                tunSeen |= tun;
                if (tun && _network.HasActiveTunRoute())
                {
                    _log.Write("tunnel technical state confirmed; pid=" + status.SingBoxPid);
                    return (await ActiveTunnelAsync(status.SingBoxPid, token))!;
                }
                await Task.Delay(_poll, timeout.Token);
            }
        }
        catch (ServiceControlException ex) { return ControlError(ex); }
        catch (Win32Exception ex) { return Error("WINDOWS_API_ERROR", "Не удалось управлять службой Windows.", ex.NativeErrorCode); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            var code = !serviceSeen ? "SERVICE_TIMEOUT" : !processSeen ? "PROCESS_TIMEOUT" :
                !tunSeen ? "TUN_TIMEOUT" : "ROUTE_TIMEOUT";
            return Error(code, "Туннель не подтвердился за 30 секунд.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _log.Exception("tunnel operation", ex); return Error("TUNNEL_START_FAILED", "Не удалось запустить туннель."); }
        finally { _operation.Release(); }
    }

    public async Task<ConnectionSnapshot> DisconnectAsync(CancellationToken token)
    {
        Interlocked.Increment(ref _networkGeneration);
        await _operation.WaitAsync(token);
        try
        {
            _log.Write("mode requested: direct");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_timeout);
            var status = _service.Query();
            if (status.State == ManagedServiceState.Missing) { ServiceAvailable = false; return Error("SERVICE_NOT_INSTALLED", SetupRequired); }
            if (status.State == ManagedServiceState.Running) _service.Stop();
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                status = _service.Query();
                if (status.State == ManagedServiceState.Stopped && status.SingBoxPid == null &&
                    !_network.HasLikelyTunInterface() && !_network.HasActiveTunRoute())
                    return await DirectAsync(token);
                await Task.Delay(_poll, timeout.Token);
            }
        }
        catch (ServiceControlException ex) { return ControlError(ex); }
        catch (Win32Exception ex) { return Error("WINDOWS_API_ERROR", "Не удалось управлять службой Windows.", ex.NativeErrorCode); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Error("SERVICE_STOP_TIMEOUT", "Отключение не подтвердилось за 30 секунд."); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _log.Exception("direct operation", ex); return Error("DIRECT_FAILED", "Не удалось отключить туннель."); }
        finally { _operation.Release(); }
    }

    private async Task<bool> WaitForStoppedAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_timeout);
        try
        {
            while (true)
            {
                var status = _service.Query();
                if (status.State == ManagedServiceState.Stopped && status.SingBoxPid == null &&
                    !_network.HasLikelyTunInterface() && !_network.HasActiveTunRoute()) return true;
                await Task.Delay(_poll, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    public (TimeSpan Elapsed, TrafficCounters? Traffic) CurrentStatistics() =>
        _session.Read(_network.ReadReliableTunTraffic());

    public async Task<ConnectionSnapshot?> RefreshIpAsync(CancellationToken token)
    {
        var requestGeneration = Interlocked.Read(ref _networkGeneration);
        if (!await _operation.WaitAsync(0, token)) return null;
        try
        {
            if (requestGeneration != Interlocked.Read(ref _networkGeneration)) return null;
            if (_last.State is not (ConnectionState.Tunnel or ConnectionState.TunnelActiveRouteConflict or
                ConnectionState.Selective)) return null;
            var status = _service.Query();
            if (status.State != ManagedServiceState.Running || status.SingBoxPid == null ||
                !_network.HasLikelyTunInterface() || !_network.HasActiveTunRoute())
                return Error("TUN_STATE_CHANGED", "Техническое состояние туннеля изменилось.");
            return await ActiveTunnelAsync(status.SingBoxPid, token, requestGeneration);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _log.Exception("ip refresh", ex); return null; }
        finally { _operation.Release(); }
    }

    private async Task<ConnectionSnapshot?> ActiveTunnelAsync(int? pid, CancellationToken token,
        long? requestGeneration = null)
    {
        var started = _last.ConnectedAt ?? DateTimeOffset.UtcNow;
        // Public-IP APIs are advisory after the service, child, TUN and route are confirmed.
        var ip = await _connection.GetPublicIPv4Async(true, token);
        var country = ip == null ? null : await _connection.GetCountryAsync(ip, token);
        if (requestGeneration != null && requestGeneration != Interlocked.Read(ref _networkGeneration))
            return null;
        if (_last.State is not (ConnectionState.Tunnel or ConnectionState.TunnelActiveRouteConflict or
                ConnectionState.Selective))
            _session.Begin(DateTimeOffset.UtcNow, _network.ReadReliableTunTraffic());
        if (_modes.ActiveServiceMode == TunnelMode.Selective)
        {
            // This GUI is not in the list, so its IP check goes direct: the home IP is expected here.
            _log.Write(ip == null ? "selective active: ip unavailable" : "selective active");
            return _last = new(ConnectionState.Selective, ip, country, ip == null ? "IP не удалось проверить" : null,
                started, null, pid);
        }
        var expected = _exitIps.ExpectedExitIp;
        if (expected == null && ip != null)
        {
            // Imported configurations do not say where traffic exits: trust the first verified tunnel.
            _exitIps.LearnExitIp(ip);
            expected = ip;
            _log.Write("exit ip learned");
        }
        var conflict = ip != null && ip != expected;
        var warning = conflict ? "Обнаружен другой VPN или конфликт маршрутизации. Внешний IP отличается от IP сервера." :
            ip == null ? "IP не удалось проверить" : null;
        _log.Write(conflict ? "tunnel active: route conflict" : ip == null ? "tunnel active: ip unavailable" : "tunnel active: expected exit ip");
        return _last = new(conflict ? ConnectionState.TunnelActiveRouteConflict : ConnectionState.Tunnel,
            ip, country, warning, started, null, pid);
    }

    private async Task<ConnectionSnapshot> DirectAsync(CancellationToken token)
    {
        _session.Reset();
        var ip = await _connection.GetPublicIPv4Async(true, token);
        var country = ip == null ? null : await _connection.GetCountryAsync(ip, token);
        _log.Write("connection verified: direct");
        return _last = new(ConnectionState.Direct, ip, country, null, null, null, null);
    }

    private ConnectionSnapshot ControlError(ServiceControlException ex)
    {
        return ex.DiagnosticCode switch
        {
            "SERVICE_NOT_INSTALLED" or "SERVICE_ACCESS_DENIED" =>
                Error(ex.DiagnosticCode, SetupRequired, ex.NativeErrorCode, ex.Api),
            "SCM_ACCESS_DENIED" =>
                Error(ex.DiagnosticCode, "Нет доступа к диспетчеру служб Windows.", ex.NativeErrorCode, ex.Api),
            "SERVICE_START_FAILED" =>
                Error(ex.DiagnosticCode, "Не удалось запустить службу VPS Tunnel.", ex.NativeErrorCode, ex.Api),
            "SERVICE_STOP_FAILED" =>
                Error(ex.DiagnosticCode, "Не удалось остановить службу VPS Tunnel.", ex.NativeErrorCode, ex.Api),
            _ => Error(ex.DiagnosticCode, "Ошибка проверки службы VPS Tunnel.", ex.NativeErrorCode, ex.Api)
        };
    }

    private ConnectionSnapshot Error(string code, string message, int? windowsError = null, string? api = null)
    {
        if (code is "SERVICE_NOT_INSTALLED" or "SERVICE_ACCESS_DENIED") ServiceAvailable = false;
        _log.Write("connection verification: " + code +
            (api == null ? "" : " api=" + api) +
            (windowsError == null ? "" : " win32=" + windowsError));
        return _last = new(ConnectionState.Error, null, null, message, null, null, null);
    }
}
