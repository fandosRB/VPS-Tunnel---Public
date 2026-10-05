using System.ComponentModel;
using System.Net;
using System.Net.Http;
using VPS.Tunnel.App.Services;
using VPS.Tunnel.Core;

var passed = 0;
await Check("DIRECT -> TUNNEL", async () =>
{
    var f = new Fixture();
    var result = await f.Coordinator.ConnectAsync(default);
    Assert(result.State == ConnectionState.Tunnel && f.Service.StartCount == 1);
});
await Check("regression: stopped service with QUERY/START/STOP requests Start", async () =>
{
    var f = new Fixture();
    Assert(f.Service.State == ManagedServiceState.Stopped && f.Service.QueryGranted &&
        f.Service.StartGranted && f.Service.StopGranted);
    var visualStates = new[] { ConnectionState.Connecting };
    var result = await f.Coordinator.ConnectAsync(default);
    Assert(visualStates[0] == ConnectionState.Connecting && f.Service.QueryCount > 0 &&
        f.Service.StartCount == 1 && result.State == ConnectionState.Tunnel &&
        result.Error != "VPS Tunnel требует завершить первоначальную настройку.");
});
await Check("TUNNEL -> DIRECT", async () =>
{
    var f = new Fixture();
    await f.Coordinator.ConnectAsync(default);
    var result = await f.Coordinator.DisconnectAsync(default);
    Assert(result.State == ConnectionState.Direct && f.Service.StopCount == 1);
});
await Check("service access denied", async () =>
{
    var f = new Fixture(); f.Service.DenyStart = true;
    Assert((await f.Coordinator.ConnectAsync(default)).Error ==
        "VPS Tunnel требует завершить первоначальную настройку.");
});
await Check("StartService failure is not setup required", async () =>
{
    var f = new Fixture(); f.Service.FailStartApi = true;
    var result = await f.Coordinator.ConnectAsync(default);
    Assert(result.State == ConnectionState.Error && result.Error !=
        "VPS Tunnel требует завершить первоначальную настройку." &&
        File.ReadAllText(f.Log.CurrentPath).Contains("SERVICE_START_FAILED api=StartServiceW win32=5"));
});
await Check("SCM access denied is distinct", async () =>
{
    var f = new Fixture(); f.Service.DenyScm = true;
    var result = await f.Coordinator.ConnectAsync(default);
    Assert(result.State == ConnectionState.Error && result.Error ==
        "Нет доступа к диспетчеру служб Windows.");
});
await Check("service timeout", async () =>
{
    var f = new Fixture(); f.Service.Stuck = true;
    Assert((await f.Coordinator.ConnectAsync(default)).State == ConnectionState.Error);
});
await Check("TUN delayed", async () =>
{
    var f = new Fixture(); f.Network.TunDelay = 3;
    Assert((await f.Coordinator.ConnectAsync(default)).State == ConnectionState.Tunnel);
});
await Check("route delayed", async () =>
{
    var f = new Fixture(); f.Network.RouteDelay = 3;
    Assert((await f.Coordinator.ConnectAsync(default)).State == ConnectionState.Tunnel);
});
await Check("IP delayed", async () =>
{
    var f = new Fixture(); f.Info.Ip = null;
    var first = await f.Coordinator.ConnectAsync(default);
    f.Info.Ip = TestNet.ExitIp;
    var second = await f.Coordinator.RefreshIpAsync(default);
    Assert(first.State == ConnectionState.Tunnel && first.PublicIPv4 == null &&
        second?.PublicIPv4 == TestNet.ExitIp);
});
await Check("IP API unavailable", async () =>
{
    var f = new Fixture(); f.Info.Ip = null;
    var result = await f.Coordinator.ConnectAsync(default);
    Assert(result.State == ConnectionState.Tunnel && result.Error == "IP не удалось проверить");
});
await Check("wrong IP + active TUN => conflict", async () =>
{
    var f = new Fixture(); f.Info.Ip = "203.0.113.44";
    Assert((await f.Coordinator.ConnectAsync(default)).State == ConnectionState.TunnelActiveRouteConflict);
});
await Check("wrong IP does not STOP", async () =>
{
    var f = new Fixture(); f.Info.Ip = "203.0.113.44";
    await f.Coordinator.ConnectAsync(default);
    Assert(f.Service.StopCount == 0 && f.Service.State == ManagedServiceState.Running);
});
await Check("A: DIRECT to TUNNEL replaces displayed IP", async () =>
{
    var f = new Fixture(); f.Info.Ip = "192.0.2.25";
    var direct = await f.Coordinator.DetectStartupAsync(default);
    f.Info.Ip = TestNet.ExitIp;
    var tunnel = await f.Coordinator.ConnectAsync(default);
    Assert(direct.PublicIPv4 == "192.0.2.25" && tunnel.State == ConnectionState.Tunnel &&
        tunnel.PublicIPv4 == TestNet.ExitIp && tunnel.Country == "Тестовая страна");
});
await Check("B: forced check bypasses old pool and cache", async () =>
{
    var created = 0;
    using var info = new ConnectionInfoService(() => new StubIpHandler(
        ++created == 1 ? "192.0.2.25" : TestNet.ExitIp));
    Assert(await info.GetPublicIPv4Async(true, default) == "192.0.2.25");
    Assert(await info.GetPublicIPv4Async(true, default) == TestNet.ExitIp);
    Assert(created == 2);
});
await Check("C: old async result ignored after mode generation change", async () =>
{
    var f = new Fixture();
    await f.Coordinator.ConnectAsync(default);
    var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    f.Info.Pending = pending;
    var refresh = f.Coordinator.RefreshIpAsync(default);
    await f.Info.RequestStarted.Task;
    var reconnect = f.Coordinator.ConnectAsync(default);
    pending.SetResult("192.0.2.25");
    Assert(await refresh == null);
    Assert((await reconnect).PublicIPv4 == TestNet.ExitIp);
});
await Check("D: TUNNEL to DIRECT replaces VPS IP", async () =>
{
    var f = new Fixture();
    await f.Coordinator.ConnectAsync(default);
    f.Info.Ip = "192.0.2.25";
    var direct = await f.Coordinator.DisconnectAsync(default);
    Assert(direct.State == ConnectionState.Direct && direct.PublicIPv4 == "192.0.2.25" &&
        f.Service.StopCount == 1);
});
await Check("E: IP timeout clears old IP but leaves tunnel active", async () =>
{
    var f = new Fixture();
    await f.Coordinator.ConnectAsync(default);
    f.Info.Ip = null;
    var result = await f.Coordinator.RefreshIpAsync(default);
    Assert(result?.State == ConnectionState.Tunnel && result.PublicIPv4 == null &&
        result.Country == null && result.Error == "IP не удалось проверить" &&
        f.Service.State == ManagedServiceState.Running && f.Service.StopCount == 0);
});
await Check("E: actual forced HTTP timeout does not return cached IP", async () =>
{
    var created = 0;
    using var info = new ConnectionInfoService(() =>
        ++created == 1 ? new StubIpHandler(TestNet.ExitIp) : new TimeoutIpHandler());
    Assert(await info.GetPublicIPv4Async(true, default) == TestNet.ExitIp);
    Assert(await info.GetPublicIPv4Async(true, default) == null && created == 2);
});
await Check("F: only verified different exit IP is route conflict", async () =>
{
    var f = new Fixture(); f.Info.Ip = null;
    Assert((await f.Coordinator.ConnectAsync(default)).State == ConnectionState.Tunnel);
    f.Info.Ip = "203.0.113.44";
    var conflict = await f.Coordinator.RefreshIpAsync(default);
    Assert(conflict?.State == ConnectionState.TunnelActiveRouteConflict &&
        conflict.PublicIPv4 == "203.0.113.44" && f.Service.StopCount == 0);
});
await Check("unknown exit IP is learned from the first tunnel, then enforced", async () =>
{
    var f = new Fixture { Info = { Ip = "203.0.113.9" } };
    var store = new InMemoryExitIpStore();
    var coordinator = new ServiceConnectionCoordinator(f.Service, f.Info, f.Network, new SessionStatisticsService(),
        f.Log, TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(5), f.Modes, store);
    var first = await coordinator.ConnectAsync(default);
    f.Info.Ip = "192.0.2.50";
    var conflict = await coordinator.RefreshIpAsync(default);
    Assert(first.State == ConnectionState.Tunnel && store.ExpectedExitIp == "203.0.113.9" &&
        conflict?.State == ConnectionState.TunnelActiveRouteConflict);
});
await Check("repeated DIRECT", async () =>
{
    var f = new Fixture();
    await f.Coordinator.DisconnectAsync(default);
    await f.Coordinator.DisconnectAsync(default);
    Assert(f.Service.StopCount == 0);
});
await Check("repeated TUNNEL", async () =>
{
    var f = new Fixture();
    await f.Coordinator.ConnectAsync(default);
    await f.Coordinator.ConnectAsync(default);
    Assert(f.Service.StartCount == 1);
});
await Check("SELECTIVE starts service with application arguments", async () =>
{
    var f = new Fixture(); f.Info.Ip = "192.0.2.25";
    var result = await f.Coordinator.ConnectAsync(TunnelMode.Selective, Sel("chrome.exe", "Telegram.exe"), false, default);
    Assert(result.State == ConnectionState.Selective && f.Service.StartCount == 1 &&
        f.Service.LastArguments!.SequenceEqual(["--selective", "chrome.exe", "Telegram.exe"]) &&
        f.Modes.ActiveServiceMode == TunnelMode.Selective);
});
await Check("SELECTIVE home IP is not a route conflict", async () =>
{
    var f = new Fixture(); f.Info.Ip = "192.0.2.25";
    var result = await f.Coordinator.ConnectAsync(TunnelMode.Selective, Sel("chrome.exe"), false, default);
    Assert(result.State == ConnectionState.Selective && result.Error == null &&
        result.PublicIPv4 == "192.0.2.25" && f.Service.StopCount == 0);
});
await Check("SELECTIVE with empty list never starts the service", async () =>
{
    var f = new Fixture();
    var result = await f.Coordinator.ConnectAsync(TunnelMode.Selective, Sel("", "not-an-exe", "C:\\x\\y.exe"), false, default);
    Assert(result.State == ConnectionState.Error && f.Service.StartCount == 0 && f.Service.QueryCount == 0);
});
await Check("TUNNEL -> SELECTIVE restarts the service", async () =>
{
    var f = new Fixture();
    await f.Coordinator.ConnectAsync(default);
    var result = await f.Coordinator.ConnectAsync(TunnelMode.Selective, Sel("chrome.exe"), false, default);
    Assert(result.State == ConnectionState.Selective && f.Service.StopCount == 1 && f.Service.StartCount == 2 &&
        f.Service.LastArguments!.SequenceEqual(["--selective", "chrome.exe"]));
});
await Check("SELECTIVE -> TUNNEL restarts without arguments", async () =>
{
    var f = new Fixture();
    await f.Coordinator.ConnectAsync(TunnelMode.Selective, Sel("chrome.exe"), false, default);
    var result = await f.Coordinator.ConnectAsync(default);
    Assert(result.State == ConnectionState.Tunnel && f.Service.StopCount == 1 && f.Service.StartCount == 2 &&
        f.Service.LastArguments == null && f.Modes.ActiveServiceMode == TunnelMode.Tunnel);
});
await Check("repeated SELECTIVE does not restart; changed list does", async () =>
{
    var f = new Fixture();
    await f.Coordinator.ConnectAsync(TunnelMode.Selective, Sel("chrome.exe"), false, default);
    await f.Coordinator.ConnectAsync(TunnelMode.Selective, Sel("chrome.exe"), false, default);
    Assert(f.Service.StartCount == 1 && f.Service.StopCount == 0);
    var result = await f.Coordinator.ConnectAsync(TunnelMode.Selective, Sel("chrome.exe", "msedge.exe"), true, default);
    Assert(result.State == ConnectionState.Selective && f.Service.StartCount == 2 && f.Service.StopCount == 1 &&
        f.Service.LastArguments!.SequenceEqual(["--selective", "chrome.exe", "msedge.exe"]));
});
await Check("SELECTIVE with only WSL starts with the WSL flag", async () =>
{
    var f = new Fixture();
    var result = await f.Coordinator.ConnectAsync(TunnelMode.Selective, new SelectiveRequest([], true), false, default);
    Assert(result.State == ConnectionState.Selective && f.Service.LastArguments!.SequenceEqual(["--selective", "--wsl"]));
    var both = await f.Coordinator.ConnectAsync(TunnelMode.Selective,
        new SelectiveRequest(["chrome.exe"], true), true, default);
    Assert(both.State == ConnectionState.Selective &&
        f.Service.LastArguments!.SequenceEqual(["--selective", "--wsl", "chrome.exe"]));
});
await Check("SELECTIVE -> DIRECT stops the service", async () =>
{
    var f = new Fixture();
    await f.Coordinator.ConnectAsync(TunnelMode.Selective, Sel("chrome.exe"), false, default);
    var result = await f.Coordinator.DisconnectAsync(default);
    Assert(result.State == ConnectionState.Direct && f.Service.StopCount == 1);
});
await Check("startup detects running SELECTIVE", async () =>
{
    var f = new Fixture(); f.Service.State = ManagedServiceState.Running;
    f.Modes.SetActiveServiceMode(TunnelMode.Selective);
    var result = await f.Coordinator.DetectStartupAsync(default);
    var refreshed = await f.Coordinator.RefreshIpAsync(default);
    Assert(result.State == ConnectionState.Selective && refreshed?.State == ConnectionState.Selective &&
        f.Service.StartCount == 0 && f.Service.StopCount == 0);
});
await Check("startup and LastMode support SELECTIVE", async () =>
{
    Assert(StartupBehavior.TargetMode(new AppSettings { StartupMode = StartupMode.Selective }) == TunnelMode.Selective &&
        StartupBehavior.TargetMode(new AppSettings { StartupMode = StartupMode.LastMode,
            LastSelectedMode = TunnelMode.Selective }) == TunnelMode.Selective &&
        StartupBehavior.ShouldInitiateTunnel(new AppSettings { StartupMode = StartupMode.Selective }, ConnectionState.Direct) &&
        StartupBehavior.ShouldRememberSelection(TunnelMode.Selective, ConnectionState.Selective) &&
        !StartupBehavior.ShouldRememberSelection(TunnelMode.Selective, ConnectionState.Error) &&
        TrayIconState.FromConnectionState(ConnectionState.Selective) == TrayIconState.Selective);
    await Task.CompletedTask;
});
await Check("SELECTIVE settings persist and are sanitized", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), "VpsTunnelSelective-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsService(path);
        await store.SaveAsync(new AppSettings { StartupMode = StartupMode.LastMode,
            LastSelectedMode = TunnelMode.Selective, ActiveServiceMode = TunnelMode.Selective,
            SelectiveApplications = ["chrome.exe", "CHROME.EXE", "..\\evil.exe", "Telegram.exe"],
            SelectiveIncludeWsl = true }, default);
        var loaded = store.Load();
        Assert(loaded.LastSelectedMode == TunnelMode.Selective && loaded.ActiveServiceMode == TunnelMode.Selective &&
            loaded.SelectiveIncludeWsl &&
            loaded.SelectiveApplications.SequenceEqual(["chrome.exe", "Telegram.exe"]));
    }
    finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
});
await Check("connection settings keep only the display name and exit IP", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), "VpsTunnelConnection-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsService(path);
        var controller = new AppSettingsCoordinator(store, new WindowsStartupService(new FakeStartupStore(),
            () => @"C:\Program Files\VPS Tunnel\VPS-Tunnel.exe"));
        controller.FinishInitialization();
        controller.LearnExitIp("192.0.2.1");
        controller.SetConnection("Мой VPS", "203.0.113.10", configured: true);
        controller.LearnExitIp("192.0.2.99"); // ignored once known
        controller.SetConnection("Новое имя", null, configured: false); // rename only
        var loaded = store.Load();
        Assert(loaded.ServerDisplayName == "Новое имя" && loaded.ExpectedExitIp == "203.0.113.10" &&
            loaded.ConnectionConfigured && !File.ReadAllText(store.SettingsPath).Contains("uuid", StringComparison.OrdinalIgnoreCase));
    }
    finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    await Task.CompletedTask;
});
await Check("startup DIRECT", async () =>
{
    var f = new Fixture();
    Assert((await f.Coordinator.DetectStartupAsync(default)).State == ConnectionState.Direct &&
        !StartupBehavior.ShouldConnect(new AppSettings { StartupMode = StartupMode.Direct }) && f.Service.StartCount == 0);
});
await Check("startup Last", async () =>
{
    var settings = new AppSettings { StartupMode = StartupMode.LastMode, LastSelectedMode = TunnelMode.Tunnel };
    Assert(StartupBehavior.ShouldConnect(settings));
    settings.LastSelectedMode = TunnelMode.Direct;
    Assert(!StartupBehavior.ShouldConnect(settings));
    await Task.CompletedTask;
});
await Check("startup TUNNEL", async () =>
{
    var settings = new AppSettings { StartupMode = StartupMode.Tunnel };
    Assert(StartupBehavior.ShouldConnect(settings));
    await Task.CompletedTask;
});
await Check("StartMinimized", async () =>
{
    var settings = new AppSettings { LaunchMinimized = true };
    Assert(StartupBehavior.ShouldHideAtLaunch(settings, automaticLaunch: true));
    Assert(!StartupBehavior.ShouldHideAtLaunch(settings, automaticLaunch: false));
    await Task.CompletedTask;
});
await Check("StartWithWindows ON uses only GUI in HKCU Run", async () =>
{
    var store = new FakeStartupStore();
    var startup = new WindowsStartupService(store);
    startup.Apply(true, @"C:\Program Files\VPS Tunnel\VPS-Tunnel.exe");
    Assert(store.Value == "\"C:\\Program Files\\VPS Tunnel\\VPS-Tunnel.exe\" --autostart" &&
        store.Name == "VPS Tunnel");
    await Task.CompletedTask;
});
await Check("StartWithWindows OFF removes Run value", async () =>
{
    var store = new FakeStartupStore();
    var startup = new WindowsStartupService(store);
    startup.Apply(true, @"C:\Program Files\VPS Tunnel\VPS-Tunnel.exe");
    startup.Apply(false);
    Assert(store.Value == null && store.DeleteCount == 1);
    await Task.CompletedTask;
});
await Check("StartMinimized OFF keeps autostart GUI visible", async () =>
{
    Assert(!StartupBehavior.ShouldHideAtLaunch(new AppSettings { LaunchMinimized = false }, true));
    await Task.CompletedTask;
});
await Check("Startup DIRECT + stopped service stays DIRECT", async () =>
{
    var f = new Fixture(); f.Info.Ip = "192.0.2.25";
    var state = (await f.Coordinator.DetectStartupAsync(default)).State;
    Assert(state == ConnectionState.Direct && !StartupBehavior.ShouldInitiateTunnel(
        new AppSettings { StartupMode = StartupMode.Direct }, state) && f.Service.StartCount == 0);
});
await Check("Startup DIRECT + running service preserves TUNNEL", async () =>
{
    var f = new Fixture(); f.Service.State = ManagedServiceState.Running;
    var state = (await f.Coordinator.DetectStartupAsync(default)).State;
    Assert(state == ConnectionState.Tunnel && !StartupBehavior.ShouldInitiateTunnel(
        new AppSettings { StartupMode = StartupMode.Direct }, state) &&
        f.Service.StartCount == 0 && f.Service.StopCount == 0);
});
await Check("Startup LAST/DIRECT does not start", async () =>
{
    var settings = new AppSettings { StartupMode = StartupMode.LastMode, LastSelectedMode = TunnelMode.Direct };
    Assert(!StartupBehavior.ShouldInitiateTunnel(settings, ConnectionState.Direct));
    await Task.CompletedTask;
});
await Check("Startup LAST/TUNNEL starts only from confirmed DIRECT", async () =>
{
    var settings = new AppSettings { StartupMode = StartupMode.LastMode, LastSelectedMode = TunnelMode.Tunnel };
    Assert(StartupBehavior.ShouldInitiateTunnel(settings, ConnectionState.Direct) &&
        !StartupBehavior.ShouldInitiateTunnel(settings, ConnectionState.Tunnel) &&
        !StartupBehavior.ShouldInitiateTunnel(settings, ConnectionState.Error));
    await Task.CompletedTask;
});
await Check("Startup TUNNEL + stopped service requests start", async () =>
{
    var f = new Fixture(); f.Info.Ip = "192.0.2.25";
    var state = (await f.Coordinator.DetectStartupAsync(default)).State;
    var settings = new AppSettings { StartupMode = StartupMode.Tunnel };
    Assert(state == ConnectionState.Direct && StartupBehavior.ShouldInitiateTunnel(settings, state));
    f.Info.Ip = TestNet.ExitIp;
    var result = await f.Coordinator.ConnectAsync(default);
    Assert(result.State == ConnectionState.Tunnel && f.Service.StartCount == 1);
});
await Check("Startup TUNNEL + running service does not start twice", async () =>
{
    var f = new Fixture(); f.Service.State = ManagedServiceState.Running;
    var state = (await f.Coordinator.DetectStartupAsync(default)).State;
    Assert(state == ConnectionState.Tunnel && !StartupBehavior.ShouldInitiateTunnel(
        new AppSettings { StartupMode = StartupMode.Tunnel }, state) && f.Service.StartCount == 0);
});
await Check("LastMode stores only successful DIRECT or TUNNEL", async () =>
{
    Assert(StartupBehavior.ShouldRememberSelection(TunnelMode.Direct, ConnectionState.Direct) &&
        StartupBehavior.ShouldRememberSelection(TunnelMode.Tunnel, ConnectionState.Tunnel) &&
        !StartupBehavior.ShouldRememberSelection(TunnelMode.Tunnel, ConnectionState.TunnelActiveRouteConflict) &&
        !StartupBehavior.ShouldRememberSelection(TunnelMode.Tunnel, ConnectionState.Error));
    await Task.CompletedTask;
});
await Check("Tray close hides, tray exit closes without tunnel stop", async () =>
{
    var f = new Fixture(); f.Service.State = ManagedServiceState.Running;
    var window = new FakeTrayWindow();
    WindowStartupPresenter.HideToTray(window);
    Assert(TrayBehavior.HideOnClose(false, true) && !TrayBehavior.HideOnClose(true, true) &&
        !TrayBehavior.HideOnClose(false, false) && f.Service.StopCount == 0 &&
        window.Actions.SequenceEqual(["Hide", "Taskbar:False"]));
    await Task.CompletedTask;
});
await Check("tray Open restores normal visible window", async () =>
{
    var window = new FakeTrayWindow();
    WindowStartupPresenter.Restore(window);
    Assert(window.Actions.SequenceEqual(["Normal", "Taskbar:True", "Show", "Activate"]));
    await Task.CompletedTask;
});
await Check("tray icon follows DIRECT/TUNNEL/CONNECTING/ERROR", async () =>
{
    Assert(TrayIconState.FromConnectionState(ConnectionState.Direct) == TrayIconState.Direct &&
        TrayIconState.FromConnectionState(ConnectionState.Tunnel) == TrayIconState.Tunnel &&
        TrayIconState.FromConnectionState(ConnectionState.Connecting) == TrayIconState.Connecting &&
        TrayIconState.FromConnectionState(ConnectionState.Error) == TrayIconState.Error &&
        TrayIconState.FromConnectionState(ConnectionState.TunnelActiveRouteConflict) == TrayIconState.Tunnel);
    await Task.CompletedTask;
});
await Check("second instance signals existing controller", async () =>
{
    var identity = "test-" + Guid.NewGuid().ToString("N");
    using var first = new SingleInstanceService(identity);
    using var second = new SingleInstanceService(identity);
    var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var window = new FakeTrayWindow();
    first.ActivationRequested += (_, _) =>
    {
        WindowStartupPresenter.Restore(window);
        opened.TrySetResult();
    };
    Assert(first.TryAcquire() && !second.TryAcquire());
    Assert(await second.ActivateExistingAsync());
    await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(window.Actions.SequenceEqual(["Normal", "Taskbar:True", "Show", "Activate"]));
});
await Check("corrupted settings return safe defaults", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), "VpsTunnelBadSettings-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    try
    {
        await File.WriteAllTextAsync(Path.Combine(path, "settings.json"), "{broken json");
        var settings = new AppSettingsService(path).Load();
        Assert(!settings.LaunchWithWindows && !settings.LaunchMinimized &&
            settings.StartupMode == StartupMode.Direct && settings.LastSelectedMode == TunnelMode.Direct &&
            settings.Theme == ThemePreference.System);
    }
    finally { Directory.Delete(path, recursive: true); }
});
await Check("settings persistence", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), "VpsTunnelSettingsTest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsService(path);
        await store.SaveAsync(new AppSettings { Theme = ThemePreference.Dark, LaunchWithWindows = true,
            LaunchMinimized = true, StartupMode = StartupMode.LastMode, LastSelectedMode = TunnelMode.Tunnel }, default);
        var loaded = store.Load();
        Assert(loaded.Theme == ThemePreference.Dark && loaded.LaunchWithWindows && loaded.LaunchMinimized &&
            loaded.StartupMode == StartupMode.LastMode && loaded.LastSelectedMode == TunnelMode.Tunnel);
    }
    finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
});
await Check("settings initialization never writes defaults", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), "VpsTunnelInit-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsService(path);
        store.Save(new AppSettings { LaunchWithWindows = true, LaunchMinimized = true,
            StartupMode = StartupMode.Tunnel, LastSelectedMode = TunnelMode.Tunnel });
        var before = File.ReadAllText(store.SettingsPath);
        var registry = new FakeStartupStore();
        var controller = new AppSettingsCoordinator(store, new WindowsStartupService(registry,
            () => @"C:\Program Files\VPS Tunnel\VPS-Tunnel.exe"));
        controller.SetStartWithWindows(false);
        controller.SetStartMinimized(false);
        controller.SetStartupMode(StartupMode.Direct);
        Assert(controller.Settings.LaunchWithWindows && controller.Settings.LaunchMinimized &&
            controller.Settings.StartupMode == StartupMode.Tunnel &&
            File.ReadAllText(store.SettingsPath) == before && registry.DeleteCount == 0);
    }
    finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    await Task.CompletedTask;
});
await Check("ON/ON/TUNNEL survives restart", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), "VpsTunnelReload-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsService(path);
        var controller = new AppSettingsCoordinator(store, new WindowsStartupService(new FakeStartupStore(),
            () => @"C:\Program Files\VPS Tunnel\VPS-Tunnel.exe"));
        controller.FinishInitialization();
        controller.SetStartWithWindows(true);
        controller.SetStartMinimized(true);
        controller.SetStartupMode(StartupMode.Tunnel);
        var reloaded = new AppSettingsCoordinator(new AppSettingsService(path),
            new WindowsStartupService(new FakeStartupStore()));
        Assert(reloaded.Settings.LaunchWithWindows && reloaded.Settings.LaunchMinimized &&
            reloaded.Settings.StartupMode == StartupMode.Tunnel);
    }
    finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    await Task.CompletedTask;
});
await Check("autostart and minimize toggles are independent", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), "VpsTunnelToggles-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsService(path);
        var controller = new AppSettingsCoordinator(store, new WindowsStartupService(new FakeStartupStore(),
            () => @"C:\Program Files\VPS Tunnel\VPS-Tunnel.exe"));
        controller.FinishInitialization();
        controller.SetStartMinimized(true);
        Assert(!controller.Settings.LaunchWithWindows && controller.Settings.LaunchMinimized);
        controller.SetStartWithWindows(true);
        Assert(controller.Settings.LaunchWithWindows && controller.Settings.LaunchMinimized);
        controller.SetStartMinimized(false);
        Assert(controller.Settings.LaunchWithWindows && !controller.Settings.LaunchMinimized);
        controller.SetStartWithWindows(false);
        Assert(!controller.Settings.LaunchWithWindows && !controller.Settings.LaunchMinimized);
    }
    finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    await Task.CompletedTask;
});
await Check("rapid settings changes preserve final state", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), "VpsTunnelRapid-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppSettingsService(path);
        var controller = new AppSettingsCoordinator(store, new WindowsStartupService(new FakeStartupStore(),
            () => @"C:\Program Files\VPS Tunnel\VPS-Tunnel.exe"));
        controller.FinishInitialization();
        for (var i = 0; i < 20; i++)
        {
            controller.SetStartMinimized(i % 2 == 0);
            controller.SetStartupMode(i % 2 == 0 ? StartupMode.Direct : StartupMode.Tunnel);
        }
        var reloaded = store.Load();
        Assert(!reloaded.LaunchMinimized && reloaded.StartupMode == StartupMode.Tunnel &&
            !File.Exists(store.SettingsPath + ".tmp"));
    }
    finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    await Task.CompletedTask;
});
await Check("autostart registers installed executable, never publish path", async () =>
{
    var fake = new FakeStartupStore();
    var startup = new WindowsStartupService(fake,
        () => @"C:\Program Files\VPS Tunnel\VPS-Tunnel.exe");
    startup.Apply(true);
    Assert(fake.Value == "\"C:\\Program Files\\VPS Tunnel\\VPS-Tunnel.exe\" --autostart" &&
        !fake.Value.Contains("publish", StringComparison.OrdinalIgnoreCase));
    await Task.CompletedTask;
});
await Check("autostart minimized creates tray without ever showing window", async () =>
{
    var settings = new AppSettings { LaunchMinimized = true };
    var window = new FakeStartupWindow { ShouldStartHidden = StartupBehavior.ShouldHideAtLaunch(settings, true),
        TrayAvailable = true };
    WindowStartupPresenter.Present(window);
    Assert(window.ConfigureCount == 1 && window.TrayInitCount == 1 && window.ShowCount == 0 &&
        window.WindowRemainsNormal);
    await Task.CompletedTask;
});
await Check("autostart without minimize shows normal window", async () =>
{
    var settings = new AppSettings { LaunchMinimized = false };
    var window = new FakeStartupWindow { ShouldStartHidden = StartupBehavior.ShouldHideAtLaunch(settings, true) };
    WindowStartupPresenter.Present(window);
    Assert(window.ConfigureCount == 0 && window.TrayInitCount == 0 && window.ShowCount == 1);
    await Task.CompletedTask;
});
await Check("manual launch ignores StartMinimized", async () =>
{
    var settings = new AppSettings { LaunchMinimized = true };
    var window = new FakeStartupWindow { ShouldStartHidden = StartupBehavior.ShouldHideAtLaunch(settings, false) };
    WindowStartupPresenter.Present(window);
    Assert(window.ConfigureCount == 0 && window.ShowCount == 1);
    await Task.CompletedTask;
});
await Check("tray failure falls back to visible normal window", async () =>
{
    var window = new FakeStartupWindow { ShouldStartHidden = true, TrayAvailable = false };
    WindowStartupPresenter.Present(window);
    Assert(window.ConfigureCount == 1 && window.TrayInitCount == 1 && window.ShowCount == 1 &&
        window.WindowRemainsNormal);
    await Task.CompletedTask;
});
Console.WriteLine($"PASS {passed} GUI integration tests");

async Task Check(string name, Func<Task> test)
{
    await test(); passed++; Console.WriteLine("PASS " + name);
}
static SelectiveRequest Sel(params string[] applications) => new(applications, false);
static void Assert(bool condition)
{
    if (!condition) throw new Exception("Assertion failed.");
}

sealed class Fixture
{
    public FakeService Service { get; } = new();
    public FakeNetwork Network { get; } = new();
    public FakeInfo Info { get; } = new();
    public InMemoryServiceModeStore Modes { get; } = new();
    public InMemoryExitIpStore ExitIps { get; set; } = new(TestNet.ExitIp);
    public SafeLog Log { get; } = new(Path.Combine(Path.GetTempPath(), "VpsTunnelTestLogs"));
    public ServiceConnectionCoordinator Coordinator { get; }
    public Fixture()
    {
        Network.Service = Service;
        Coordinator = new(Service, Info, Network, new SessionStatisticsService(), Log,
            TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(5), Modes, ExitIps);
    }
}

static class TestNet
{
    public const string ExitIp = "198.51.100.7";
}

sealed class FakeService : IServiceControl
{
    public ManagedServiceState State = ManagedServiceState.Stopped;
    public int StartCount, StopCount, QueryCount;
    public IReadOnlyList<string>? LastArguments;
    public bool QueryGranted = true, StartGranted = true, StopGranted = true;
    public bool DenyStart, FailStartApi, DenyScm, Stuck;
    public ManagedServiceStatus Query()
    {
        QueryCount++;
        if (DenyScm) throw new ServiceControlException("SCM_ACCESS_DENIED", "OpenSCManagerW", 5);
        if (!QueryGranted) throw new ServiceControlException("SERVICE_ACCESS_DENIED", "OpenServiceW", 5);
        return new(State, State == ManagedServiceState.Running ? 10 : 0,
            State == ManagedServiceState.Running ? 11 : null);
    }
    public void Start(IReadOnlyList<string>? arguments = null)
    {
        LastArguments = arguments;
        if (DenyStart || !StartGranted) throw new ServiceControlException("SERVICE_ACCESS_DENIED", "OpenServiceW", 5);
        if (FailStartApi) throw new ServiceControlException("SERVICE_START_FAILED", "StartServiceW", 5);
        StartCount++;
        if (!Stuck) State = ManagedServiceState.Running;
        else State = ManagedServiceState.StartPending;
    }
    public void Stop() { StopCount++; State = ManagedServiceState.Stopped; }
}

sealed class FakeNetwork : INetworkStatusService
{
    public FakeService Service = null!;
    public int TunDelay, RouteDelay;
    private int _tunChecks, _routeChecks;
    public bool HasLikelyTunInterface() => Service.State == ManagedServiceState.Running && ++_tunChecks > TunDelay;
    public bool HasActiveTunRoute() => Service.State == ManagedServiceState.Running && ++_routeChecks > RouteDelay;
    public TrafficCounters? ReadReliableTunTraffic() => new(100, 200);
}

sealed class FakeInfo : IConnectionInfoService
{
    public string? Ip = TestNet.ExitIp;
    public TaskCompletionSource<string?>? Pending;
    public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<string?> GetPublicIPv4Async(bool forceRefresh, CancellationToken token)
    {
        if (Pending is { } pending)
        {
            Pending = null;
            RequestStarted.TrySetResult();
            return pending.Task;
        }
        return Task.FromResult(Ip);
    }
    public Task<string?> GetCountryAsync(string ip, CancellationToken token) => Task.FromResult<string?>("Тестовая страна");
}

sealed class StubIpHandler(string ip) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ip) });
}

sealed class TimeoutIpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated timeout"));
}

sealed class FakeStartupStore : IStartupValueStore
{
    public string? Name, Value;
    public int DeleteCount;
    public void Set(string name, string value) { Name = name; Value = value; }
    public void Delete(string name) { Name = name; Value = null; DeleteCount++; }
}

sealed class FakeStartupWindow : IStartupWindow
{
    public bool ShouldStartHidden { get; set; }
    public bool TrayAvailable { get; set; }
    public bool WindowRemainsNormal { get; private set; } = true;
    public int ConfigureCount, TrayInitCount, ShowCount;
    public void ConfigureHiddenStartup() { ConfigureCount++; }
    public bool InitializeTrayWithoutShowing() { TrayInitCount++; return TrayAvailable; }
    public void ShowNormally() { ShowCount++; }
}

sealed class FakeTrayWindow : ITrayWindow
{
    public List<string> Actions { get; } = new();
    public void SetNormalState() => Actions.Add("Normal");
    public void SetTaskbarVisible(bool visible) => Actions.Add("Taskbar:" + visible);
    public void ShowWindow() => Actions.Add("Show");
    public void ActivateWindow() => Actions.Add("Activate");
    public void HideWindow() => Actions.Add("Hide");
}
