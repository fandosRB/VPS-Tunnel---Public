using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using VPS.Tunnel.Core;

namespace VPS.Tunnel.Service;

public sealed class SelectiveConfigurationWriter : ISelectiveConfigurationWriter
{
    // Under Program Files, so ordinary users cannot pre-create or redirect it.
    private static readonly string RuntimeDirectory = Path.Combine(AppContext.BaseDirectory, "Runtime");
    public static readonly string RuntimeConfigurationPath = Path.Combine(RuntimeDirectory, "selective-config.json");

    public string Write(SelectiveRequest request)
    {
        var configuration = SelectiveConfigBuilder.Build(
            File.ReadAllText(TunnelServiceLifecycle.ConfigurationPath, Encoding.UTF8), request);

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

        var temporary = RuntimeConfigurationPath + ".tmp";
        File.WriteAllText(temporary, configuration, new UTF8Encoding(false));
        File.Move(temporary, RuntimeConfigurationPath, overwrite: true);
        return RuntimeConfigurationPath;
    }
}
