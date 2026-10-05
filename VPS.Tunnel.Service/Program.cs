using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using VPS.Tunnel.Service;

// SCM is the only control plane. A plain start runs the fixed sing-box configuration;
// a start with "--selective <app.exe>..." runs the derived SELECTIVE configuration.
using var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "VpsTunnelService")
    .ConfigureLogging(logging => logging.ClearProviders())
    .ConfigureServices(services =>
    {
        services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(35));
        services.AddSingleton<IServiceEventLog, FileServiceEventLog>();
        services.AddSingleton<IFileProbe, PhysicalFileProbe>();
        services.AddSingleton<IChildProcessRunner, SingBoxProcessRunner>();
        services.AddSingleton<ServiceStartArguments>();
        services.AddSingleton<ISelectiveConfigurationWriter, SelectiveConfigurationWriter>();
        // Replaces the default lifetime registered by UseWindowsService to see StartService arguments.
        if (WindowsServiceHelpers.IsWindowsService())
            services.AddSingleton<IHostLifetime, ArgumentCapturingServiceLifetime>();
        services.AddSingleton<TunnelServiceLifecycle>(provider => new TunnelServiceLifecycle(
            provider.GetRequiredService<IFileProbe>(),
            provider.GetRequiredService<IChildProcessRunner>(),
            provider.GetRequiredService<IServiceEventLog>(),
            provider.GetRequiredService<IHostApplicationLifetime>().StopApplication,
            arguments: provider.GetRequiredService<ServiceStartArguments>(),
            selective: provider.GetRequiredService<ISelectiveConfigurationWriter>()));
        services.AddHostedService<TunnelHostedService>();
    })
    .Build();

await host.RunAsync();

