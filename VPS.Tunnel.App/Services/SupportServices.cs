using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using VPS.Tunnel.Core;

namespace VPS.Tunnel.App.Services;

public sealed class AppSettingsService : IAppSettingsService
{
    public static string BaseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPS Tunnel");
    private readonly string _directory;
    private readonly object _saveGate = new();
    public AppSettingsService(string? directory = null) => _directory = directory ?? BaseDirectory;
    public string SettingsPath => Path.Combine(_directory, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new AppSettings();
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
            if (!Enum.IsDefined(loaded.Theme)) loaded.Theme = ThemePreference.System;
            if (!Enum.IsDefined(loaded.StartupMode)) loaded.StartupMode = StartupMode.Direct;
            if (!Enum.IsDefined(loaded.LastSelectedMode)) loaded.LastSelectedMode = TunnelMode.Direct;
            if (loaded.ActiveServiceMode != TunnelMode.Selective) loaded.ActiveServiceMode = TunnelMode.Tunnel;
            loaded.SelectiveApplications = SelectiveApplications.Normalize(loaded.SelectiveApplications);
            return loaded;
        }
        catch (JsonException) { return new AppSettings(); }
        catch (IOException) { return new AppSettings(); }
        catch (UnauthorizedAccessException) { return new AppSettings(); }
    }

    public void Save(AppSettings settings)
    {
        lock (_saveGate)
        {
            Directory.CreateDirectory(_directory);
            var temporary = SettingsPath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonSerializer.Serialize(settings, Options));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(SettingsPath)) File.Replace(temporary, SettingsPath, null);
            else File.Move(temporary, SettingsPath);
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Save(settings);
        return Task.CompletedTask;
    }
}

public sealed class SafeLog
{
    private readonly object _sync = new();
    private readonly string _directory;
    public SafeLog(string? directory = null) => _directory = directory ?? Path.Combine(AppSettingsService.BaseDirectory, "Logs");
    public string CurrentPath => Path.Combine(_directory, "app.log");

    // Callers pass only constant event names, PIDs, and verification outcomes.
    public void Write(string eventName)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                if (File.Exists(CurrentPath) && new FileInfo(CurrentPath).Length > 1_000_000)
                    File.Move(CurrentPath, Path.Combine(_directory, "app.previous.log"), overwrite: true);
                File.AppendAllText(CurrentPath, $"{DateTimeOffset.UtcNow:O} {eventName}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Exception(string operation, Exception error) => Write(operation + " failed: " + error.GetType().Name);
}

public sealed class ConnectionInfoService : IConnectionInfoService, IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private readonly Func<HttpMessageHandler> _ipHandlerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedIp;
    private DateTimeOffset _ipChecked;
    private string? _cachedCountry;
    private string? _countryForIp;
    private DateTimeOffset _countryChecked;

    public ConnectionInfoService(Func<HttpMessageHandler>? ipHandlerFactory = null) =>
        _ipHandlerFactory = ipHandlerFactory ?? (() => new SocketsHttpHandler { UseProxy = false });

    public async Task<string?> GetPublicIPv4Async(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var age = DateTimeOffset.UtcNow - _ipChecked;
            if (!forceRefresh && age < TimeSpan.FromSeconds(30))
                return _cachedIp;
            _ipChecked = DateTimeOffset.UtcNow;
            _cachedIp = null;
            try
            {
                // A post-route verification must not reuse a socket opened in the previous mode.
                using var freshHandler = _ipHandlerFactory();
                using var freshClient = new HttpClient(freshHandler) { Timeout = TimeSpan.FromSeconds(5) };
                using var response = await freshClient.GetAsync("https://api.ipify.org", cancellationToken);
                response.EnsureSuccessStatusCode();
                var raw = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                if (IPAddress.TryParse(raw, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
                    _cachedIp = address.ToString();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // Public-IP outage is displayed as unavailable, never as a verified mode.
            }
            return _cachedIp;
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetCountryAsync(string ipv4, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(ipv4, out var address) || address.AddressFamily != AddressFamily.InterNetwork) return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (ipv4 == _countryForIp && DateTimeOffset.UtcNow - _countryChecked < TimeSpan.FromMinutes(30))
                return _cachedCountry;
            _countryForIp = ipv4;
            _countryChecked = DateTimeOffset.UtcNow;
            _cachedCountry = null;
            try
            {
                using var response = await _http.GetAsync($"https://ipapi.co/{ipv4}/country/", cancellationToken);
                response.EnsureSuccessStatusCode();
                var code = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                // Two-letter ISO code, shown in the Windows display language (e.g. "DE" -> "Германия").
                if (code.Length == 2 && code.All(char.IsAsciiLetter))
                {
                    try { _cachedCountry = new RegionInfo(code).DisplayName; }
                    catch (ArgumentException) { _cachedCountry = code.ToUpperInvariant(); }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { }
            return _cachedCountry;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}

public sealed class NetworkStatusService : INetworkStatusService
{
    // Count only an unambiguous sing-box named adapter. Another VPN's Wintun
    // counter must never be attributed to this session.
    private static NetworkInterface[] Candidates() => NetworkInterface.GetAllNetworkInterfaces().Where(adapter =>
        adapter.OperationalStatus == OperationalStatus.Up &&
        (adapter.Name.Contains("sing-box", StringComparison.OrdinalIgnoreCase) ||
         adapter.Description.Contains("sing-box", StringComparison.OrdinalIgnoreCase))).ToArray();

    public bool HasLikelyTunInterface() => Candidates().Length == 1;
    public bool HasActiveTunRoute()
    {
        var adapters = Candidates();
        if (adapters.Length != 1) return false;
        try
        {
            var index = adapters[0].GetIPProperties().GetIPv4Properties()?.Index;
            if (index == null) return false;
            // Enumerate routes owned by sing-box-tun. GetBestInterfaceEx would report
            // another VPN's preferred route and falsely declare our TUN absent.
            var size = 0;
            _ = GetIpForwardTable(IntPtr.Zero, ref size, false);
            if (size < 4 || size > 4_000_000) return false;
            var table = Marshal.AllocHGlobal(size);
            try
            {
                if (GetIpForwardTable(table, ref size, false) != 0) return false;
                var count = Marshal.ReadInt32(table);
                if (count < 0 || count > (size - 4) / 56) return false;
                for (var row = 0; row < count; row++)
                {
                    var entry = IntPtr.Add(table, 4 + row * 56);
                    var destination = unchecked((uint)Marshal.ReadInt32(entry));
                    var mask = unchecked((uint)Marshal.ReadInt32(entry, 4));
                    var routeIndex = Marshal.ReadInt32(entry, 16);
                    if (routeIndex == index &&
                        (destination == 0 && mask == 0 || mask == 0x00000080)) return true;
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        catch (NetworkInformationException) { return false; }
    }
    public TrafficCounters? ReadReliableTunTraffic()
    {
        var adapters = Candidates();
        if (adapters.Length != 1) return null;
        try
        {
            var statistics = adapters[0].GetIPv4Statistics();
            return new(statistics.BytesReceived, statistics.BytesSent);
        }
        catch (NetworkInformationException) { return null; }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpForwardTable(IntPtr table, ref int size, bool order);
}

public sealed class SessionStatisticsService : ISessionStatisticsService
{
    private DateTimeOffset? _started;
    private TrafficCounters? _baseline;
    public void Begin(DateTimeOffset started, TrafficCounters? baseline) { _started = started; _baseline = baseline; }
    public void Reset() { _started = null; _baseline = null; }
    public (TimeSpan Elapsed, TrafficCounters? Delta) Read(TrafficCounters? current)
    {
        if (_started == null) return (TimeSpan.Zero, null);
        TrafficCounters? delta = _baseline != null && current != null
            ? new(Math.Max(0, current.ReceivedBytes - _baseline.ReceivedBytes),
                  Math.Max(0, current.SentBytes - _baseline.SentBytes))
            : null;
        return (DateTimeOffset.UtcNow - _started.Value, delta);
    }
}
