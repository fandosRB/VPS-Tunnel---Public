using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VPS.Tunnel.App.Services;
using VPS.Tunnel.Core;

namespace VPS.Tunnel.App;

public partial class MainWindow : Window, IStartupWindow, ITrayWindow
{
    private readonly SafeLog _log = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly IConnectionInfoService _connectionInfo = new ConnectionInfoService();
    private readonly ThemeService _themes = new();
    private readonly WindowsStartupService _startupService = new();
    private readonly AppSettingsCoordinator _settingsCoordinator;
    private readonly SetupLauncher _setupLauncher = new();
    private readonly AppSettings _settings;
    private readonly ServiceConnectionCoordinator _connection;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _statisticsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ConnectionState _state = ConnectionState.Unknown;
    private bool _isInitializing = true;
    private readonly bool _startHidden;
    private bool _busy;
    private bool _exitRequested;
    private int _refreshTicks;
    private ConnectionSnapshot _displayed = new(ConnectionState.Unknown, null, null, null, null, null, null);
    private TrayService? _tray;

    public MainWindow()
    {
        _settingsCoordinator = new AppSettingsCoordinator(_settingsService, _startupService);
        _settings = _settingsCoordinator.Settings;
        _startHidden = StartupBehavior.ShouldHideAtLaunch(_settings,
            (Application.Current as App)?.IsAutomaticLaunch == true);
        _connection = new ServiceConnectionCoordinator(new WindowsServiceControl(), _connectionInfo,
            new NetworkStatusService(), new SessionStatisticsService(), _log, modes: _settingsCoordinator,
            exitIps: _settingsCoordinator);
        InitializeComponent();
        WindowsStartupCheck.IsChecked = _settings.LaunchWithWindows;
        MinimizedCheck.IsChecked = _settings.LaunchMinimized;
        StartupComboBox.SelectedIndex = (int)_settings.StartupMode;
        UpdateSelectiveAppsText();
        UpdateServerText();
        WindowsStartupCheck.Checked += StartWithWindowsChanged;
        WindowsStartupCheck.Unchecked += StartWithWindowsChanged;
        MinimizedCheck.Checked += StartMinimizedChanged;
        MinimizedCheck.Unchecked += StartMinimizedChanged;
        StartupComboBox.SelectionChanged += StartupMode_Changed;
        _themes.Changed += ThemeChanged;
        _statisticsTimer.Tick += StatisticsTimer_Tick;
        _statisticsTimer.Start();
        SetTheme(_settings.Theme, save: false);
        RenderState(new(ConnectionState.Unknown, null, null, null, null, null, null));
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Application.Current.SessionEnding += OnSessionEnding;
        StateChanged += (_, _) => WindowFrame.CornerRadius =
            WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(12);
        _isInitializing = false;
        _settingsCoordinator.FinishInitialization();
        _log.Write("application start");
    }

    public bool ShouldStartHidden => _startHidden;

    public void ConfigureHiddenStartup()
    {
        ShowInTaskbar = false;
        ShowActivated = false;
    }

    public bool InitializeTrayWithoutShowing()
    {
        new WindowInteropHelper(this).EnsureHandle();
        return _tray?.IsAvailable == true;
    }

    public void ShowNormally()
    {
        WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        ShowActivated = true;
        Show();
    }

    public async Task InitializeStartupAsync()
    {
        await RunOperationAsync(() => _connection.DetectStartupAsync(_lifetime.Token), notifyError: false);
        if (!ConnectionConfigWriter.IsConfigured(_settings))
        {
            // First run on a new computer: ask for the user's own server instead of connecting.
            if (IsVisible) await EditConnectionAsync(welcome: true);
            return;
        }
        if (StartupBehavior.ShouldInitiateTunnel(_settings, _state))
            await SwitchModeAsync(StartupBehavior.TargetMode(_settings), notifyError: false, rememberSelection: false);
    }

    private async void ConnectionSettings_Click(object sender, RoutedEventArgs e) => await EditConnectionAsync(welcome: false);

    /// <summary>Lets the user enter their own server; returns true when a configuration was saved.</summary>
    private async Task<bool> EditConnectionAsync(bool welcome)
    {
        if (_busy) return false;
        var configured = ConnectionConfigWriter.IsConfigured(_settings);
        var dialog = new ConnectionSettingsWindow(_settings.ServerDisplayName, configured, welcome) { Owner = IsVisible ? this : null };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result) return false;
        if (result.Configuration == null)
        {
            TryUpdateSettings(() => _settingsCoordinator.SetConnection(result.DisplayName, null, configured: false));
            UpdateServerText();
            return false;
        }

        ConfigWriteResult written;
        try { written = await ConnectionConfigWriter.ApplyAsync(result.Configuration, _lifetime.Token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            _log.Exception("connection config write", ex);
            written = ConfigWriteResult.Failed;
        }
        if (written != ConfigWriteResult.Written)
        {
            MessageBox.Show(this, written == ConfigWriteResult.Cancelled
                    ? "Настройки не сохранены: для записи нужно разрешение администратора."
                    : "Не удалось сохранить настройки подключения.",
                "VPS Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _log.Write("connection config written");
        var expectedExitIp = result.ServerHost == null ? null : await ResolveIPv4Async(result.ServerHost);
        TryUpdateSettings(() => _settingsCoordinator.SetConnection(result.DisplayName, expectedExitIp, configured: true));
        UpdateServerText();

        // A running tunnel still uses the old configuration until the service restarts.
        if (_state is ConnectionState.Tunnel or ConnectionState.TunnelActiveRouteConflict)
            await SwitchModeAsync(TunnelMode.Tunnel, notifyError: true, restart: true);
        else if (_state == ConnectionState.Selective)
            await SwitchModeAsync(TunnelMode.Selective, notifyError: true, restart: true);
        return true;
    }

    private static async Task<string?> ResolveIPv4Async(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out var literal))
            return literal.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? literal.ToString() : null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, timeout.Token);
            return addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return null; // learned from the first verified tunnel instead
        }
    }

    private void UpdateServerText()
    {
        var configured = ConnectionConfigWriter.IsConfigured(_settings);
        ServerValue.Text = _settings.ServerDisplayName ?? (configured ? "Настроен" : "Не настроен");
        ServerValue.ToolTip = _settings.ServerDisplayName;
        ConnectionSettingsButton.Content = new TextBlock
        {
            Text = configured ? "Изменить" : "Настроить",
            Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush")
        };
    }

    private async void Direct_Click(object sender, RoutedEventArgs e) =>
        await SwitchModeAsync(TunnelMode.Direct, notifyError: true);

    private async void Tunnel_Click(object sender, RoutedEventArgs e) =>
        await SwitchModeAsync(TunnelMode.Tunnel, notifyError: true);

    private async void Selective_Click(object sender, RoutedEventArgs e) => await SelectiveRequestedAsync();

    private async Task SelectiveRequestedAsync()
    {
        if (_busy) return;
        if (_settingsCoordinator.SelectiveRequest.IsEmpty && !EditSelectiveApplications()) return;
        if (_settingsCoordinator.SelectiveRequest.IsEmpty) return;
        await SwitchModeAsync(TunnelMode.Selective, notifyError: true);
    }

    private async void SelectiveApps_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !EditSelectiveApplications() || _state != ConnectionState.Selective) return;
        // An active SELECTIVE session picks up the new list only after a service restart.
        if (!_settingsCoordinator.SelectiveRequest.IsEmpty)
            await SwitchModeAsync(TunnelMode.Selective, notifyError: true, restart: true);
        else
            await SwitchModeAsync(TunnelMode.Direct, notifyError: true);
    }

    /// <summary>Returns true when the user saved a changed list.</summary>
    private bool EditSelectiveApplications()
    {
        var editor = new SelectiveAppsWindow(_settingsCoordinator.SelectiveRequest) { Owner = IsVisible ? this : null };
        if (editor.ShowDialog() != true) return false;
        var updated = editor.Result;
        if (updated.IncludeWsl == _settings.SelectiveIncludeWsl &&
            updated.Applications.SequenceEqual(_settings.SelectiveApplications, StringComparer.OrdinalIgnoreCase)) return false;
        TryUpdateSettings(() => _settingsCoordinator.SetSelective(updated.Applications, updated.IncludeWsl));
        UpdateSelectiveAppsText();
        return true;
    }

    private void UpdateSelectiveAppsText()
    {
        var count = _settings.SelectiveApplications.Count;
        var wsl = _settings.SelectiveIncludeWsl;
        SelectiveAppsText.Text = count == 0 && !wsl ? "Выбрать" :
            wsl ? $"Изменить ({count} + WSL)" : $"Изменить ({count})";
        var names = _settings.SelectiveApplications.Concat(wsl ? ["весь WSL"] : Array.Empty<string>()).ToList();
        SelectiveButton.ToolTip = names.Count == 0
            ? "Через VPS — только выбранные программы"
            : "Через VPS: " + string.Join(", ", names);
    }

    private async Task SwitchModeAsync(TunnelMode mode, bool notifyError, bool rememberSelection = true,
        bool restart = false)
    {
        if (mode != TunnelMode.Direct && !ConnectionConfigWriter.IsConfigured(_settings) &&
            !await EditConnectionAsync(welcome: true)) return;
        var snapshot = await RunOperationAsync(
            () => mode == TunnelMode.Direct
                ? _connection.DisconnectAsync(_lifetime.Token)
                : _connection.ConnectAsync(mode, _settingsCoordinator.SelectiveRequest, restart, _lifetime.Token),
            notifyError, mode == TunnelMode.Direct ? ConnectionState.Disconnecting : ConnectionState.Connecting);
        if (rememberSelection && snapshot != null && StartupBehavior.ShouldRememberSelection(mode, snapshot.State))
            TryUpdateSettings(() => _settingsCoordinator.RememberMode(mode));
    }

    private async Task<ConnectionSnapshot?> RunOperationAsync(Func<Task<ConnectionSnapshot>> action, bool notifyError,
        ConnectionState pending = ConnectionState.Unknown)
    {
        if (_busy) return null;
        _busy = true;
        DirectButton.IsEnabled = TunnelButton.IsEnabled = SelectiveButton.IsEnabled = SelectiveAppsButton.IsEnabled =
            ConnectionSettingsButton.IsEnabled = false;
        RenderState(new(pending, null, null, null, null, null, null));
        try
        {
            var snapshot = await action();
            if (_lifetime.IsCancellationRequested) return null;
            RenderState(snapshot);
            if (notifyError && snapshot.State == ConnectionState.Error)
                MessageBox.Show(this, snapshot.Error ?? "Не удалось подтвердить соединение.",
                    "VPS Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (notifyError && snapshot.State == ConnectionState.TunnelActiveRouteConflict)
                MessageBox.Show(this, snapshot.Error ?? "Обнаружен другой VPN или конфликт маршрутизации.",
                    "VPS Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return snapshot;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Exception("UI operation", ex);
            var message = "Не удалось проверить соединение. Подробности доступны в журнале приложения.";
            RenderState(new(ConnectionState.Error, null, null, message, null, null, null));
            if (notifyError) MessageBox.Show(this, message, "VPS Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            DirectButton.IsEnabled = TunnelButton.IsEnabled = SelectiveButton.IsEnabled = SelectiveAppsButton.IsEnabled =
                ConnectionSettingsButton.IsEnabled = true;
        }
        return null;
    }

    private void RenderState(ConnectionSnapshot snapshot)
    {
        _displayed = snapshot;
        _state = snapshot.State;
        bool tunnel = snapshot.State is ConnectionState.Tunnel or ConnectionState.TunnelActiveRouteConflict;
        bool selective = snapshot.State == ConnectionState.Selective;
        bool protectedState = tunnel || selective;
        bool direct = snapshot.State == ConnectionState.Direct;
        StatusTitle.Text = snapshot.State switch
        {
            ConnectionState.Tunnel => "TUNNEL",
            ConnectionState.Selective => "SELECTIVE",
            ConnectionState.TunnelActiveRouteConflict => "ТУННЕЛЬ АКТИВЕН",
            ConnectionState.Direct => "DIRECT",
            ConnectionState.Connecting => "ПОДКЛЮЧЕНИЕ",
            ConnectionState.Disconnecting => "ОТКЛЮЧЕНИЕ",
            ConnectionState.Error => "ОШИБКА",
            _ => "ПРОВЕРКА"
        };
        StatusTitle.FontSize = snapshot.State is ConnectionState.Connecting or ConnectionState.Disconnecting or
            ConnectionState.TunnelActiveRouteConflict ? 16 : 25;
        StatusSubtitle.Text = snapshot.State switch
        {
            ConnectionState.Tunnel => "Защищённое соединение",
            ConnectionState.Selective => "Выбранные программы через VPS",
            ConnectionState.TunnelActiveRouteConflict => "Конфликт VPN или маршрута",
            ConnectionState.Direct => "Прямое соединение",
            ConnectionState.Error => "Ошибка подключения",
            _ => "Определяем состояние"
        };
        StatusBlock.ToolTip = snapshot.Error;
        SetupButton.Visibility = _connection.ServiceAvailable ? Visibility.Collapsed : Visibility.Visible;
        _tray?.SetState(snapshot.State);
        StatusIcon.Data = (Geometry)FindResource(selective ? "AppsIcon" : tunnel ? "ShieldIcon" : "GlobeIcon");
        StatusIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
            protectedState ? "AccentBrush" : "NeutralBrush");
        StatusRing.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
            protectedState ? "AccentBrush" : "NeutralBrush");
        StatusRing.Effect = protectedState ? new DropShadowEffect
        {
            Color = ((SolidColorBrush)FindResource("AccentBrush")).Color,
            BlurRadius = 20, ShadowDepth = 0, Opacity = 0.22
        } : null;
        DirectButton.SetResourceReference(BackgroundProperty, direct ? "DirectActiveBrush" : "CardBrush");
        DirectButton.SetResourceReference(BorderBrushProperty, direct ? "NeutralBrush" : "BorderBrush");
        TunnelButton.SetResourceReference(BackgroundProperty, tunnel ? "AccentBrush" : "CardBrush");
        TunnelButton.SetResourceReference(BorderBrushProperty, tunnel ? "AccentBrush" : "BorderBrush");
        TunnelButton.SetResourceReference(ForegroundProperty, tunnel ? "WhiteBrush" : "TextBrush");
        TunnelHint.SetResourceReference(TextBlock.ForegroundProperty, tunnel ? "WhiteBrush" : "SecondaryBrush");
        SelectiveButton.SetResourceReference(BackgroundProperty, selective ? "AccentBrush" : "CardBrush");
        SelectiveButton.SetResourceReference(BorderBrushProperty, selective ? "AccentBrush" : "BorderBrush");
        SelectiveButton.SetResourceReference(ForegroundProperty, selective ? "WhiteBrush" : "TextBrush");
        SelectiveHint.SetResourceReference(TextBlock.ForegroundProperty, selective ? "WhiteBrush" : "SecondaryBrush");
        IpValue.Text = snapshot.PublicIPv4 ?? (snapshot.State is ConnectionState.Connecting or ConnectionState.Disconnecting
            ? "Проверка..." : "Не удалось проверить");
        CountryValue.Text = snapshot.Country ?? "—";
        StatusBlock.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.75, 1, TimeSpan.FromMilliseconds(180)));
        RenderStatistics();
    }

    private async void StatisticsTimer_Tick(object? sender, EventArgs e)
    {
        RenderStatistics();
        if (++_refreshTicks % 30 != 0 || _busy || _lifetime.IsCancellationRequested) return;
        try
        {
            var refreshed = await _connection.RefreshIpAsync(_lifetime.Token);
            if (refreshed != null && !_busy && !_lifetime.IsCancellationRequested) RenderState(refreshed);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void RenderStatistics()
    {
        var stats = _connection.CurrentStatistics();
        var active = _state is ConnectionState.Tunnel or ConnectionState.TunnelActiveRouteConflict or ConnectionState.Selective;
        ElapsedValue.Text = active ? stats.Elapsed.ToString(@"hh\:mm\:ss") : "00:00:00";
        DownloadValue.Text = active && stats.Traffic != null ? FormatBytes(stats.Traffic.ReceivedBytes) : "—";
        UploadValue.Text = active && stats.Traffic != null ? FormatBytes(stats.Traffic.SentBytes) : "—";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double size = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }

    private void SystemTheme_Click(object sender, RoutedEventArgs e) => SetTheme(ThemePreference.System);
    private void LightTheme_Click(object sender, RoutedEventArgs e) => SetTheme(ThemePreference.Light);
    private void DarkTheme_Click(object sender, RoutedEventArgs e) => SetTheme(ThemePreference.Dark);
    private void SetTheme(ThemePreference preference, bool save = true)
    {
        if (_isInitializing && save) return;
        _settings.Theme = preference;
        _themes.Apply(preference);
        if (save) TryUpdateSettings(() => _settingsCoordinator.SetTheme(preference));
    }

    private void ThemeChanged(object? sender, EventArgs e)
    {
        foreach (var (button, preference) in new[] {
            (SystemThemeButton, ThemePreference.System), (LightThemeButton, ThemePreference.Light),
            (DarkThemeButton, ThemePreference.Dark) })
        {
            button.SetResourceReference(BackgroundProperty,
                _settings.Theme == preference ? "NeutralSoftBrush" : "CardBrush");
            button.SetResourceReference(ForegroundProperty,
                _settings.Theme == preference ? "TextBrush" : "SecondaryBrush");
        }
        // Reapply dynamic resources after replacing the complete theme dictionary.
        RenderState(_displayed);
    }

    private void StartWithWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        var enabled = WindowsStartupCheck.IsChecked == true;
        try
        {
            _settingsCoordinator.SetStartWithWindows(enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Exception("autostart update", ex);
            _isInitializing = true;
            WindowsStartupCheck.IsChecked = _settings.LaunchWithWindows;
            _isInitializing = false;
            MessageBox.Show(this, "Не удалось изменить автозапуск Windows.", "VPS Tunnel",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StartMinimizedChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        TryUpdateSettings(() => _settingsCoordinator.SetStartMinimized(MinimizedCheck.IsChecked == true));
    }

    private void StartupMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || StartupComboBox.SelectedIndex < 0) return;
        TryUpdateSettings(() => _settingsCoordinator.SetStartupMode((StartupMode)StartupComboBox.SelectedIndex));
    }

    private void TryUpdateSettings(Action update)
    {
        try { update(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Exception("settings save", ex);
            MessageBox.Show(this, "Не удалось сохранить настройки VPS Tunnel.", "VPS Tunnel",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var setup = _setupLauncher.Launch();
            await setup.WaitForExitAsync(_lifetime.Token);
            if (setup.ExitCode != 0) throw new InvalidOperationException("Setup failed");
            var installed = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "VPS Tunnel", "VPS-Tunnel.exe");
            _startupService.Apply(_settings.LaunchWithWindows, installed);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installed)
                { UseShellExecute = true });
            _exitRequested = true;
            Close();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Exception("setup launch", ex);
            MessageBox.Show(this, "Не удалось выполнить первоначальную настройку VPS Tunnel.",
                "VPS Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!TrayBehavior.HideOnClose(_exitRequested, _tray?.IsAvailable == true)) return;
        e.Cancel = true;
        WindowStartupPresenter.HideToTray(this);
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) => _exitRequested = true;

    private void OpenFromTray() => WindowStartupPresenter.Restore(this);

    void ITrayWindow.SetNormalState() => WindowState = WindowState.Normal;
    void ITrayWindow.SetTaskbarVisible(bool visible) => ShowInTaskbar = visible;
    void ITrayWindow.ShowWindow() => Show();
    void ITrayWindow.ActivateWindow() => Activate();
    void ITrayWindow.HideWindow() => Hide();

    public void RestoreFromExternalLaunch() => OpenFromTray();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _tray = new TrayService(this);
        _tray.OpenRequested += (_, _) => OpenFromTray();
        _tray.DirectRequested += async (_, _) => await SwitchModeAsync(TunnelMode.Direct, notifyError: true);
        _tray.TunnelRequested += async (_, _) => await SwitchModeAsync(TunnelMode.Tunnel, notifyError: true);
        _tray.SelectiveRequested += async (_, _) => await SelectiveRequestedAsync();
        _tray.ExitRequested += (_, _) => { _exitRequested = true; Close(); };
        _tray.Initialize();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int preference = 2;
            _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref preference, sizeof(int));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        _statisticsTimer.Stop();
        _themes.Changed -= ThemeChanged;
        Application.Current.SessionEnding -= OnSessionEnding;
        _themes.Dispose();
        _tray?.Dispose();
        (_connectionInfo as IDisposable)?.Dispose();
        _log.Write("application stop");
        base.OnClosed(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
