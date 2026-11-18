using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using VPS.Tunnel.Core;

namespace VPS.Tunnel.Service;

/// <summary>Writes the SELECTIVE and EXCLUDE configurations derived from C:\sing-box\config.json.</summary>
public sealed class SelectiveConfigurationWriter : ISelectiveConfigurationWriter
{
    // Under Program Files, so ordinary users cannot pre-create or redirect it.
    private static readonly string RuntimeDirectory = Path.Combine(AppContext.BaseDirectory, "Runtime");
    public static readonly string RuntimeConfigurationPath = Path.Combine(RuntimeDirectory, "selective-config.json");
    public static readonly string ExcludeConfigurationPath = Path.Combine(RuntimeDirectory, "exclude-config.json");

    public string Write(SelectiveRequest request) =>
        WriteProtected(RuntimeConfigurationPath, SelectiveConfigBuilder.Build(ReadBase(), request));

    public ExcludeWriteResult WriteExclude(ExcludeRequest request)
    {
        // sing-box compares process_path literally, so use the on-disk spelling; skip programs that are gone.
        var entries = new List<string>();
        var skipped = new List<string>();
        foreach (var entry in request.Entries)
        {
            if (!ExcludedApplications.IsPath(entry)) entries.Add(entry);
            else if (CanonicalPath(entry) is { } canonical) entries.Add(canonical);
            else skipped.Add(entry);
        }
        if (entries.Count == 0)
            throw new InvalidDataException("Ни одна программа из списка исключений не найдена на диске.");
        var path = WriteProtected(ExcludeConfigurationPath,
            ExcludeConfigBuilder.Build(ReadBase(), new ExcludeRequest(entries.Distinct(StringComparer.OrdinalIgnoreCase).ToList())));
        return new(path, skipped);
    }

    private static string ReadBase() => File.ReadAllText(TunnelServiceLifecycle.ConfigurationPath, Encoding.UTF8);

    private static string? CanonicalPath(string path)
    {
        if (!File.Exists(path)) return null;
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!.ToUpperInvariant();
        foreach (var part in full[current.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = new DirectoryInfo(current).EnumerateFileSystemInfos(part).FirstOrDefault();
            if (match == null) return full;
            current = Path.Combine(current, match.Name);
        }
        return current;
    }

    private static string WriteProtected(string target, string configuration)
    {
        // The derived file holds the same credentials as config.json: SYSTEM and Administrators only.
        Directory.CreateDirectory(RuntimeDirectory);
        if (new DirectoryInfo(RuntimeDirectory).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Runtime directory is a reparse point.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(RuntimeDirectory).SetAccessControl(security);

        var temporary = target + ".tmp";
        File.WriteAllText(temporary, configuration, new UTF8Encoding(false));
        File.Move(temporary, target, overwrite: true);
        return target;
    }
}
