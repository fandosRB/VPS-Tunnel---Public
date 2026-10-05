using Microsoft.Extensions.Hosting;

namespace VPS.Tunnel.Service;

public sealed class TunnelHostedService(TunnelServiceLifecycle lifecycle) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => lifecycle.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => lifecycle.StopAsync(cancellationToken);
}

