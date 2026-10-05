using System.Text.Json.Nodes;
using VPS.Tunnel.Core;
using VPS.Tunnel.Service;

var passed = 0;

await Case("executable missing", async () =>
{
    var test = new Fixture { Files = { Executable = false } };
    await Throws<FileNotFoundException>(() => test.Service.StartAsync(CancellationToken.None));
    Check(test.Runner.StartCount == 0, "no process on missing exe");
});

await Case("config missing", async () =>
{
    var test = new Fixture { Files = { Configuration = false } };
    await Throws<FileNotFoundException>(() => test.Service.StartAsync(CancellationToken.None));
    Check(test.Runner.StartCount == 0, "no process on missing config");
});

await Case("successful child start", async () =>
{
    var test = new Fixture();
    await test.Service.StartAsync(CancellationToken.None);
    Check(test.Runner.StartCount == 1 && test.Log.Events.Contains("SingBoxStarted"), "one child started");
    await test.Service.StopAsync(CancellationToken.None);
});

await Case("unexpected child exit", async () =>
{
    var test = new Fixture();
    await test.Service.StartAsync(CancellationToken.None);
    test.Runner.Child.CompleteExit();
    await test.ApplicationStopped.Task.WaitAsync(TimeSpan.FromSeconds(1));
    Check(test.Log.Events.Contains("SingBoxExited"), "unexpected exit logged");
    await test.Service.StopAsync(CancellationToken.None);
});

await Case("normal graceful stop", async () =>
{
    var test = new Fixture();
    await test.Service.StartAsync(CancellationToken.None);
    await test.Service.StopAsync(CancellationToken.None);
    Check(test.Runner.Child.GracefulRequests == 1, "one graceful request");
    Check(test.Runner.Child.ForcedCount == 0, "no forced fallback");
    Check(test.Log.Events.Contains("GracefulStopSucceeded"), "graceful success logged");
});

await Case("graceful timeout uses forced fallback", async () =>
{
    var test = new Fixture();
    test.Runner.Child.ExitOnGraceful = false;
    await test.Service.StartAsync(CancellationToken.None);
    await test.Service.StopAsync(CancellationToken.None);
    Check(test.Runner.Child.ForcedCount == 1, "forced fallback after deadline");
    Check(test.Log.Events.Contains("FORCED_TERMINATION"), "fallback event logged");
});

await Case("repeated stop", async () =>
{
    var test = new Fixture();
    await test.Service.StartAsync(CancellationToken.None);
    await test.Service.StopAsync(CancellationToken.None);
    await test.Service.StopAsync(CancellationToken.None);
    Check(test.Runner.Child.GracefulRequests == 1, "idempotent stop");
});

await Case("cancellation before start", async () =>
{
    var test = new Fixture();
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    await Throws<OperationCanceledException>(() => test.Service.StartAsync(cancel.Token));
    Check(test.Runner.StartCount == 0, "no child on cancellation");
});

await Case("conflicting existing process", async () =>
{
    var test = new Fixture { Runner = { Conflict = true } };
    await Throws<InvalidOperationException>(() => test.Service.StartAsync(CancellationToken.None));
    Check(test.Runner.StartCount == 0 && test.Log.Events.Contains("SingBoxConflict"), "conflict not adopted or killed");
});

await Case("plain start runs the verified TUNNEL configuration", async () =>
{
    var test = new Fixture();
    await test.Service.StartAsync(CancellationToken.None);
    Check(test.Runner.ConfigurationPath == TunnelServiceLifecycle.ConfigurationPath && test.Writer.Calls == 0,
        "base config without selective arguments");
    await test.Service.StopAsync(CancellationToken.None);
});

await Case("selective start runs the derived configuration", async () =>
{
    var test = new Fixture();
    test.Arguments.Set(["VpsTunnelService", "--selective", "chrome.exe", "Telegram.exe"]);
    await test.Service.StartAsync(CancellationToken.None);
    Check(test.Runner.ConfigurationPath == FakeWriter.Path &&
        test.Writer.Request!.Applications.SequenceEqual(["chrome.exe", "Telegram.exe"]) && !test.Writer.Request.IncludeWsl &&
        test.Log.Events.Contains("SelectiveModeRequested"), "derived config passed to sing-box");
    await test.Service.StopAsync(CancellationToken.None);
});

await Case("selective start rejects unsafe application names", async () =>
{
    foreach (var bad in new[] { "..\\x.exe", "C:\\x.exe", "x.dll", "a\"b.exe", " x.exe" })
    {
        var test = new Fixture();
        test.Arguments.Set(["--selective", "chrome.exe", bad]);
        await Throws<ArgumentException>(() => test.Service.StartAsync(CancellationToken.None));
        Check(test.Runner.StartCount == 0 && test.Writer.Calls == 0, "no child for " + bad);
    }
    var empty = new Fixture();
    empty.Arguments.Set(["--selective"]);
    await Throws<ArgumentException>(() => empty.Service.StartAsync(CancellationToken.None));
    Check(empty.Runner.StartCount == 0, "no child for empty list");
});

await Case("selective configuration failure starts nothing", async () =>
{
    var test = new Fixture { Writer = { Fail = true } };
    test.Arguments.Set(["--selective", "chrome.exe"]);
    await Throws<InvalidDataException>(() => test.Service.StartAsync(CancellationToken.None));
    Check(test.Runner.StartCount == 0, "no child when config cannot be derived");
});

const string BaseConfig = """
    {
      // comments and trailing commas are accepted by sing-box
      "inbounds": [{ "type": "tun", "tag": "tun-in", "auto_route": true }],
      "outbounds": [
        { "type": "vless", "tag": "proxy", "server": "203.0.113.1", "uuid": "secret-uuid" },
        { "type": "direct", "tag": "direct" },
      ],
      "route": { "rules": [{ "action": "sniff" }], "final": "proxy" }
    }
    """;

await Case("builder routes listed processes to proxy and the rest direct", async () =>
{
    var root = JsonNode.Parse(SelectiveConfigBuilder.Build(BaseConfig, Apps("chrome.exe")))!.AsObject();
    var route = root["route"]!.AsObject();
    var rules = route["rules"]!.AsArray();
    var added = rules[^1]!.AsObject();
    Check(route["final"]!.GetValue<string>() == "direct" && route["auto_detect_interface"]!.GetValue<bool>() &&
        rules.Count == 2 && rules[0]!["action"]!.GetValue<string>() == "sniff" &&
        added["outbound"]!.GetValue<string>() == "proxy" &&
        added["process_name"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(["chrome.exe"]) &&
        root["outbounds"]!.AsArray().Count == 2 &&
        root["outbounds"]![0]!["uuid"]!.GetValue<string>() == "secret-uuid", "selective routing");
    await Task.CompletedTask;
});

await Case("builder adds a direct outbound when missing", async () =>
{
    var config = """{ "outbounds": [{ "type": "vless", "tag": "vps" }] }""";
    var root = JsonNode.Parse(SelectiveConfigBuilder.Build(config, Apps("chrome.exe")))!.AsObject();
    var outbounds = root["outbounds"]!.AsArray();
    Check(outbounds.Count == 2 && outbounds[1]!["type"]!.GetValue<string>() == "direct" &&
        root["route"]!["final"]!.GetValue<string>() == SelectiveConfigBuilder.AddedDirectTag &&
        root["route"]!["rules"]![0]!["outbound"]!.GetValue<string>() == "vps", "direct outbound added");
    await Task.CompletedTask;
});

await Case("builder refuses a configuration without a proxy outbound", async () =>
{
    var config = """{ "outbounds": [{ "type": "direct", "tag": "direct" }], "route": { "final": "direct" } }""";
    await Throws<InvalidDataException>(() => Task.Run(() => SelectiveConfigBuilder.Build(config, Apps("chrome.exe"))));
});

await Case("start arguments round-trip", async () =>
{
    var args = SelectiveApplications.ToStartArguments(Apps("chrome.exe", "Telegram.exe"));
    var wsl = SelectiveApplications.ParseStartArguments(
        SelectiveApplications.ToStartArguments(new SelectiveRequest(["chrome.exe"], true)));
    var wslOnly = SelectiveApplications.ParseStartArguments(["--selective", "--wsl"]);
    Check(SelectiveApplications.ParseStartArguments(args)!.Applications.SequenceEqual(["chrome.exe", "Telegram.exe"]) &&
        wsl!.IncludeWsl && wsl.Applications.SequenceEqual(["chrome.exe"]) &&
        wslOnly!.IncludeWsl && wslOnly.Applications.Count == 0 &&
        SelectiveApplications.ParseStartArguments([]) == null &&
        SelectiveApplications.ParseStartArguments(["VpsTunnelService"]) == null, "round-trip");
    await Task.CompletedTask;
});

await Case("selective start with WSL passes the flag to the configuration", async () =>
{
    var test = new Fixture();
    test.Arguments.Set(["--selective", "--wsl"]);
    await test.Service.StartAsync(CancellationToken.None);
    Check(test.Writer.Request!.IncludeWsl && test.Writer.Request.Applications.Count == 0 &&
        test.Runner.ConfigurationPath == FakeWriter.Path, "WSL-only selective");
    await test.Service.StopAsync(CancellationToken.None);
});

await Case("builder routes WSL sources to proxy but never the TUN address", async () =>
{
    var config = """
        {
          "inbounds": [{ "type": "tun", "tag": "tun-in", "address": ["172.19.0.1/30", "fdfe:dcba:9876::1/126"] }],
          "outbounds": [{ "type": "vless", "tag": "proxy" }, { "type": "direct", "tag": "direct" }],
          "route": { "final": "proxy" }
        }
        """;
    var rules = JsonNode.Parse(SelectiveConfigBuilder.Build(config, new SelectiveRequest(["chrome.exe"], true)))!
        ["route"]!["rules"]!.AsArray();
    var wsl = rules[1]!.AsObject();
    var parts = wsl["rules"]!.AsArray();
    Check(rules.Count == 2 && rules[0]!["process_name"] != null &&
        wsl["type"]!.GetValue<string>() == "logical" && wsl["mode"]!.GetValue<string>() == "and" &&
        wsl["outbound"]!.GetValue<string>() == "proxy" &&
        parts[0]!["source_ip_cidr"]![0]!.GetValue<string>() == SelectiveConfigBuilder.WslSourceRange &&
        parts[1]!["invert"]!.GetValue<bool>() &&
        parts[1]!["source_ip_cidr"]!.AsArray().Select(n => n!.GetValue<string>())
            .SequenceEqual(["172.19.0.1/30", "fdfe:dcba:9876::1/126"]), "WSL rule excludes TUN");

    var legacy = """
        { "inbounds": [{ "type": "tun", "inet4_address": "172.19.0.1/30" }],
          "outbounds": [{ "type": "vless", "tag": "proxy" }], "route": { "final": "proxy" } }
        """;
    var legacyRules = JsonNode.Parse(SelectiveConfigBuilder.Build(legacy, new SelectiveRequest([], true)))!
        ["route"]!["rules"]!.AsArray();
    Check(legacyRules.Count == 1 &&
        legacyRules[0]!["rules"]![1]!["source_ip_cidr"]![0]!.GetValue<string>() == "172.19.0.1/30", "legacy TUN field");
    await Task.CompletedTask;
});

await Case("builder refuses WSL routing without a known TUN address", async () =>
{
    var config = """{ "inbounds": [{ "type": "tun" }], "outbounds": [{ "type": "vless", "tag": "proxy" }] }""";
    await Throws<InvalidDataException>(() => Task.Run(() =>
        SelectiveConfigBuilder.Build(config, new SelectiveRequest([], true))));
});

const string Link = "vless://b831381d-6324-4d53-ad4f-8cda48b30811@203.0.113.10:8443?type=tcp&security=reality" +
    "&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&fp=firefox&sni=www.microsoft.com&sid=0123abcd" +
    "&flow=xtls-rprx-vision&spx=%2F#%D0%9C%D0%BE%D0%B9%20VPS";

await Case("vless link is parsed into a profile", async () =>
{
    var profile = VlessLink.Parse(Link);
    Check(profile == new ConnectionProfile("Мой VPS", "203.0.113.10", 8443, "b831381d-6324-4d53-ad4f-8cda48b30811",
        "www.microsoft.com", "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", "0123abcd", "firefox", "xtls-rprx-vision") &&
        profile.ServerIPv4 == "203.0.113.10", "all fields");
    var domain = VlessLink.Parse("vless://b831381d-6324-4d53-ad4f-8cda48b30811@vpn.example.com?security=reality" +
        "&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sni=example.org");
    Check(domain.Port == 443 && domain.Name == "vpn.example.com" && domain.ServerIPv4 == null &&
        domain.Fingerprint == "chrome" && domain.Flow == "" && domain.ShortId == "", "defaults");
    await Task.CompletedTask;
});

await Case("unsupported or broken links are rejected with a message", async () =>
{
    foreach (var bad in new[]
    {
        "https://example.com",
        "vless://not-a-uuid@203.0.113.10:443?security=reality&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sni=a.com",
        "vless://b831381d-6324-4d53-ad4f-8cda48b30811@203.0.113.10:443?security=tls&sni=a.com",
        "vless://b831381d-6324-4d53-ad4f-8cda48b30811@203.0.113.10:443?security=reality&type=ws&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sni=a.com",
        "vless://b831381d-6324-4d53-ad4f-8cda48b30811@203.0.113.10:443?security=reality&pbk=short&sni=a.com",
        "vless://b831381d-6324-4d53-ad4f-8cda48b30811@203.0.113.10:443?security=reality&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sni=a.com&sid=xyz"
    })
    {
        try { VlessLink.Parse(bad); throw new Exception("accepted: " + bad); }
        catch (FormatException ex) { Check(ex.Message.Length > 0, "message for " + bad); }
    }
    await Task.CompletedTask;
});

await Case("template builds a TUNNEL config that SELECTIVE can derive from", async () =>
{
    var config = SingBoxConfigTemplate.Build(VlessLink.Parse(Link));
    var root = JsonNode.Parse(config)!.AsObject();
    var proxy = root["outbounds"]![0]!;
    Check(SingBoxConfigTemplate.ValidateImported(config) == null &&
        proxy["server"]!.GetValue<string>() == "203.0.113.10" && proxy["server_port"]!.GetValue<int>() == 8443 &&
        proxy["tls"]!["reality"]!["public_key"]!.GetValue<string>() == "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0" &&
        root["route"]!["final"]!.GetValue<string>() == SingBoxConfigTemplate.ProxyTag &&
        root["inbounds"]![0]!["interface_name"]!.GetValue<string>().Contains("sing-box"), "template");
    var selective = JsonNode.Parse(SelectiveConfigBuilder.Build(config, new SelectiveRequest(["chrome.exe"], true)))!;
    Check(selective["route"]!["final"]!.GetValue<string>() == "direct" &&
        selective["route"]!["rules"]!.AsArray().Count == 5, "selective from template");
    await Task.CompletedTask;
});

await Case("imported configs must be sing-box JSON with a TUN inbound", async () =>
{
    Check(SingBoxConfigTemplate.ValidateImported("{broken") != null &&
        SingBoxConfigTemplate.ValidateImported("""{ "outbounds": [{ "type": "direct" }] }""") != null &&
        SingBoxConfigTemplate.ValidateImported(BaseConfig) == null, "import validation");
    await Task.CompletedTask;
});

Console.WriteLine($"PASS: {passed} service tests; fake child only, no sing-box or TUN started");

async Task Case(string name, Func<Task> action)
{
    await action();
    Console.WriteLine("PASS: " + name);
    passed++;
}

static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

static SelectiveRequest Apps(params string[] names) => new(names, false);

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("TEST FAIL: " + message);
}

sealed class Fixture
{
    public FakeFiles Files { get; } = new();
    public FakeRunner Runner { get; } = new();
    public FakeLog Log { get; } = new();
    public TaskCompletionSource ApplicationStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ServiceStartArguments Arguments { get; } = new();
    public FakeWriter Writer { get; } = new();
    public TunnelServiceLifecycle Service => _service ??= new(Files, Runner, Log,
        () => ApplicationStopped.TrySetResult(), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100),
        Arguments, Writer);
    private TunnelServiceLifecycle? _service;
}

sealed class FakeFiles : IFileProbe
{
    public bool Executable { get; set; } = true;
    public bool Configuration { get; set; } = true;
    public bool Exists(string path) => path == TunnelServiceLifecycle.ExecutablePath ? Executable :
        path == TunnelServiceLifecycle.ConfigurationPath && Configuration;
}

sealed class FakeRunner : IChildProcessRunner
{
    public bool Conflict { get; set; }
    public int StartCount { get; private set; }
    public FakeChild Child { get; } = new();
    public bool HasConflictingSingBox() => Conflict;
    public string? ConfigurationPath { get; private set; }
    public IManagedChild Start(string configurationPath) { StartCount++; ConfigurationPath = configurationPath; return Child; }
}

sealed class FakeWriter : ISelectiveConfigurationWriter
{
    public const string Path = @"C:\Program Files\VPS Tunnel\Service\Runtime\selective-config.json";
    public bool Fail { get; set; }
    public int Calls { get; private set; }
    public SelectiveRequest? Request { get; private set; }
    public string Write(SelectiveRequest request)
    {
        Calls++;
        Request = request;
        if (Fail) throw new InvalidDataException("Proxy outbound not found.");
        return Path;
    }
}

sealed class FakeChild : IManagedChild
{
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Id => 1234;
    public bool HasExited => _exited.Task.IsCompleted;
    public bool ExitOnGraceful { get; set; } = true;
    public int GracefulRequests { get; private set; }
    public int ForcedCount { get; private set; }
    public Task WaitForExitAsync(CancellationToken token) => _exited.Task.WaitAsync(token);
    public bool RequestGracefulStop()
    {
        GracefulRequests++;
        if (ExitOnGraceful) CompleteExit();
        return true;
    }
    public void ForceTerminate() { ForcedCount++; CompleteExit(); }
    public void CompleteExit() => _exited.TrySetResult();
    public void Dispose() { }
}

sealed class FakeLog : IServiceEventLog
{
    public List<string> Events { get; } = [];
    public void Write(string eventName, int? pid = null, Exception? exception = null) => Events.Add(eventName);
}

