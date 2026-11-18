using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace VPS.Tunnel.Service;

public sealed class ConfigurationCheckException(string message) : Exception(message);

/// <summary>Runs "sing-box check -c file" before a derived configuration is used.</summary>
public sealed partial class SingBoxConfigurationChecker : IConfigurationChecker
{
    public void Check(string configurationPath)
    {
        var info = new ProcessStartInfo(TunnelServiceLifecycle.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(TunnelServiceLifecycle.ExecutablePath)!
        };
        info.ArgumentList.Add("check");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(configurationPath);
        using var process = Process.Start(info) ?? throw new ConfigurationCheckException("sing-box check не запустился.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20_000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new ConfigurationCheckException("sing-box check не ответил за 20 секунд.");
        }
        if (process.ExitCode == 0) return;
        throw new ConfigurationCheckException(FirstLine(error.Result) ?? FirstLine(output.Result) ??
            "sing-box check завершился с кодом " + process.ExitCode + ".");
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscape();

    private static string? FirstLine(string text)
    {
        var line = AnsiEscape().Replace(text, "").Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0);
        return line == null ? null : line.Length > 300 ? line[..300] + "…" : line;
    }
}

/// <summary>
/// The service log is for administrators only; the GUI reads the last start error from here to explain
/// a failed mode switch. Holds one short message, never configuration contents.
/// </summary>
public sealed class ServiceStatusFile : IServiceStatusFile
{
    public static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VPS Tunnel", "Status");
    public static readonly string ErrorPath = Path.Combine(DirectoryPath, "last-error.txt");

    public void WriteStartError(string message)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (new DirectoryInfo(DirectoryPath).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(DirectoryPath).SetAccessControl(security);
        File.WriteAllText(ErrorPath, message, new UTF8Encoding(false));
    }

    public void Clear()
    {
        if (File.Exists(ErrorPath)) File.Delete(ErrorPath);
    }
}
