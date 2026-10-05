using System.Text;

namespace VPS.Tunnel.Service;

public sealed class FileServiceEventLog : IServiceEventLog
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VPS Tunnel", "Logs");
    private static readonly string LogPath = Path.Combine(DirectoryPath, "service.log");
    private readonly object _sync = new();

    public void Write(string eventName, int? pid = null, Exception? exception = null)
    {
        // Never include child stdout/stderr, config contents, arguments or exception messages.
        var line = $"{DateTimeOffset.UtcNow:O} {eventName}";
        if (pid.HasValue) line += $" PID={pid.Value}";
        if (exception != null) line += $" Type={exception.GetType().Name} HResult={exception.HResult}";
        lock (_sync)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}

