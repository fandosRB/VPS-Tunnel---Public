using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using VPS.Tunnel.Core;

namespace VPS.Tunnel.App.Services;

public enum ConfigWriteResult { Written, Cancelled, Failed }

/// <summary>
/// Writes C:\sing-box\config.json. The GUI runs unelevated, so it hands the new file to a second,
/// elevated instance of itself (one UAC prompt); that instance validates and installs it.
/// </summary>
public static class ConnectionConfigWriter
{
    public const string Argument = "--write-config";
    public const string EngineDirectory = @"C:\sing-box";
    public static readonly string ConfigPath = Path.Combine(EngineDirectory, "config.json");
    private const string TemporaryPrefix = "vps-tunnel-config-";

    /// <summary>True when a configuration is known to exist (written by this GUI, or visible on disk).</summary>
    public static bool IsConfigured(AppSettings settings) => settings.ConnectionConfigured || File.Exists(ConfigPath);

    public static async Task<ConfigWriteResult> ApplyAsync(string configuration, CancellationToken token)
    {
        // The per-user temp folder is private to this user; the elevated copy runs as the same user.
        var temporary = Path.Combine(Path.GetTempPath(), TemporaryPrefix + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(temporary, configuration, new UTF8Encoding(false), token);
        try
        {
            var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("No process path."))
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            info.ArgumentList.Add(Argument);
            info.ArgumentList.Add(temporary);
            Process process;
            try { process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null."); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return ConfigWriteResult.Cancelled; } // UAC "No"
            using (process)
            {
                await process.WaitForExitAsync(token);
                return process.ExitCode == 0 ? ConfigWriteResult.Written : ConfigWriteResult.Failed;
            }
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Entry point of the elevated instance. Returns the process exit code.</summary>
    public static int RunElevated(string source)
    {
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 2;
            // Accept only a file this GUI created in the user's own temp folder.
            var full = Path.GetFullPath(source);
            var name = Path.GetFileName(full);
            if (!string.Equals(Path.GetDirectoryName(full)?.TrimEnd('\\'), Path.GetTempPath().TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(TemporaryPrefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal) ||
                new FileInfo(full).Length > 1_000_000) return 3;
            var configuration = File.ReadAllText(full, Encoding.UTF8);
            if (SingBoxConfigTemplate.ValidateImported(configuration) != null) return 4;

            Directory.CreateDirectory(EngineDirectory);
            var staged = ConfigPath + ".new";
            File.WriteAllText(staged, configuration, new UTF8Encoding(false));
            Protect(staged);
            if (File.Exists(ConfigPath))
            {
                // Keep the previous configuration next to it, protected the same way.
                File.Replace(staged, ConfigPath, ConfigPath + ".bak", ignoreMetadataErrors: true);
                Protect(ConfigPath + ".bak");
            }
            else File.Move(staged, ConfigPath);
            Protect(ConfigPath);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 1;
        }
    }

    /// <summary>The configuration holds credentials: SYSTEM (the service) and Administrators only.</summary>
    private static void Protect(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
