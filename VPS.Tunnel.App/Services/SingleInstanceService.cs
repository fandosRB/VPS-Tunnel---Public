using System.IO;
using System.IO.Pipes;

namespace VPS.Tunnel.App.Services;

public sealed class SingleInstanceService : IDisposable
{
    private readonly string _semaphoreName;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private Semaphore? _semaphore;
    private bool _ownsSlot;

    public event EventHandler? ActivationRequested;

    public SingleInstanceService(string identity)
    {
        _semaphoreName = @"Local\VpsTunnelGui-" + identity;
        _pipeName = "VpsTunnelGui-" + identity;
    }

    public bool TryAcquire()
    {
        _semaphore = new Semaphore(1, 1, _semaphoreName);
        if (!_semaphore.WaitOne(0))
        {
            _semaphore.Dispose();
            _semaphore = null;
            return false;
        }
        _ownsSlot = true;
        _ = ListenAsync(_lifetime.Token);
        return true;
    }

    public async Task<bool> ActivateExistingAsync(CancellationToken token = default)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(200, token);
                await client.WriteAsync(new byte[] { 1 }, token);
                await client.FlushAsync(token);
                return true;
            }
            catch (TimeoutException) { }
            catch (IOException) { }
            if (attempt < 9) await Task.Delay(100, token);
        }
        return false;
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                var buffer = new byte[1];
                if (await server.ReadAsync(buffer, token) == 1 && buffer[0] == 1)
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        if (_ownsSlot) _semaphore?.Release();
        _semaphore?.Dispose();
        _lifetime.Dispose();
    }
}
