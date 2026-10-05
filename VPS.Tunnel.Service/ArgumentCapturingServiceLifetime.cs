using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VPS.Tunnel.Service;

/// <summary>
/// Records the StartService arguments. The host waits for OnStart before starting hosted
/// services, so the lifecycle always sees the arguments of the current start.
/// </summary>
public sealed class ArgumentCapturingServiceLifetime(
    ServiceStartArguments arguments,
    IHostEnvironment environment,
    IHostApplicationLifetime applicationLifetime,
    ILoggerFactory loggerFactory,
    IOptions<HostOptions> hostOptions,
    IOptions<WindowsServiceLifetimeOptions> serviceOptions)
    : WindowsServiceLifetime(environment, applicationLifetime, loggerFactory, hostOptions, serviceOptions)
{
    protected override void OnStart(string[] args)
    {
        arguments.Set(args);
        base.OnStart(args);
    }
}
