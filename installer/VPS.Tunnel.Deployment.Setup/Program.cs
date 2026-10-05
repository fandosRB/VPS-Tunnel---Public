using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace VPS.Tunnel.Deployment.Setup;

internal static class Program
{
    private const string ServiceName = "VpsTunnelService";
    internal const string ProductName = "VPS Tunnel";
    internal const string ProductVersion = "1.1.1";
    private const string RuntimeDownload = "https://dotnet.microsoft.com/download/dotnet/10.0";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VPS Tunnel";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ShortcutName = "VPS Tunnel.lnk";
    private static readonly string AppDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);
    private static readonly string ServiceDirectory = Path.Combine(AppDirectory, "Service");
    private static readonly string GuiExe = Path.Combine(AppDirectory, "VPS-Tunnel.exe");
    private static readonly string SetupExe = Path.Combine(AppDirectory, "VPS-Tunnel-Setup.exe");
    private static readonly string EngineDirectory = @"C:\sing-box";
    private static readonly string EngineVersionDirectory = Path.Combine(EngineDirectory, "sing-box-1.14.1-windows-amd64");
    private static readonly string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName, "Logs");
    private static readonly string ServiceExe = Path.Combine(ServiceDirectory, "VPS.Tunnel.Service.exe");
    private static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ShortcutName);
    private static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), ShortcutName);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        try
        {
            if (args is ["--uninstall"]) return Uninstall();
            // Used by the GUI's "Настроить" button: re-register the service for this user, no file changes.
            if (args is ["--repair-service"]) { RegisterService(); return 0; }
            if (args.Length != 0) throw new ArgumentException("Недопустимый параметр установки.");
            return Install();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось выполнить операцию: " + ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int Install()
    {
        if (!DesktopRuntimeInstalled())
        {
            if (MessageBox.Show("Для VPS Tunnel нужна бесплатная платформа Microsoft .NET 10 Desktop Runtime (x64).\n\n" +
                    "Открыть страницу загрузки? После её установки запустите эту программу ещё раз.",
                    ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(RuntimeDownload) { UseShellExecute = true });
            return 1;
        }

        var update = File.Exists(GuiExe);
        using var options = new InstallOptionsForm(update,
            startMenu: true, desktop: !update || File.Exists(DesktopShortcut));
        if (options.ShowDialog() != DialogResult.OK) return 1;

        Cursor.Current = Cursors.WaitCursor;
        CloseRunningGui();
        using (var scm = OpenScm(0x3))
        using (var existing = new ServiceHandle(OpenService(scm.Handle, ServiceName, 0xF01FF)))
        {
            if (!existing.IsInvalid && !ServicePointsToExpectedLocation())
                throw new InvalidOperationException("На этом компьютере уже есть служба VPS Tunnel из другого расположения.");
            if (!existing.IsInvalid) StopIfRunning(existing.Handle); // an update briefly turns the tunnel off
        }

        ExtractPayload("payload/gui/", AppDirectory);
        ExtractPayload("payload/service/", ServiceDirectory);
        ExtractPayload("payload/sing-box/", EngineDirectory); // config.json is never part of the payload
        Directory.CreateDirectory(LogDirectory);
        ProtectDirectory(AppDirectory, true);
        ProtectDirectory(ServiceDirectory, true);
        // C:\sing-box holds config.json with the keys: SYSTEM and Administrators only. The engine
        // folder has no secrets and stays readable, e.g. for building the installer without elevation.
        ProtectDirectory(EngineDirectory, false);
        ProtectDirectory(EngineVersionDirectory, true);
        ProtectDirectory(LogDirectory, false);
        ProtectTree(AppDirectory);
        ProtectTree(EngineDirectory);

        RegisterService();
        CopySelf();
        RegisterUninstaller();
        RemoveShortcuts(); // also removes per-user copies made by hand, so nothing is duplicated
        if (options.StartMenu) CreateShortcut(StartMenuShortcut);
        if (options.Desktop) CreateShortcut(DesktopShortcut);
        Cursor.Current = Cursors.Default;
        // The service is left stopped: installing performs no network operation.

        if (options.Launch) LaunchUnelevated(GuiExe);
        else MessageBox.Show(update ? "VPS Tunnel обновлён." : "VPS Tunnel установлен. При первом запуске укажите свой сервер (ссылку vless://).",
            ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    private static int Uninstall()
    {
        // A program cannot delete the folder it runs from: continue from a temporary copy.
        if (RunsFromAppDirectory())
        {
            RelaunchFromTemp();
            return 0;
        }
        try
        {
            var answer = MessageBox.Show("Удалить VPS Tunnel с этого компьютера?\n\n" +
                    "«Да» — удалить программу и настройки подключения (сервер и ключи).\n" +
                    "«Нет» — удалить программу, но сохранить настройки подключения для переустановки.",
                    ProductName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Cancel) return 1;

            CloseRunningGui();
            using (var scm = OpenScm(0x1))
            using (var service = new ServiceHandle(OpenService(scm.Handle, ServiceName, 0xF01FF)))
            {
                if (!service.IsInvalid)
                {
                    StopIfRunning(service.Handle);
                    if (!DeleteService(service.Handle)) Throw("Не удалось удалить службу.");
                }
            }
            RemoveShortcuts();
            RemoveAutostart();
            Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
            DeleteEngine(removeConfiguration: answer == DialogResult.Yes);
            DeleteManagedDirectory(AppDirectory);
            MessageBox.Show("VPS Tunnel удалён." + (answer == DialogResult.No ? " Настройки подключения сохранены в C:\\sing-box." : ""),
                ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        finally { ScheduleSelfDeleteIfTemporary(); }
    }

    private static void RegisterService()
    {
        using var scm = OpenScm(0x3);
        using var existing = new ServiceHandle(OpenService(scm.Handle, ServiceName, 0xF01FF));
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Не удалось определить пользователя Windows.");
        var imagePath = '"' + ServiceExe + '"';
        if (existing.IsInvalid)
        {
            using var created = new ServiceHandle(CreateService(scm.Handle, ServiceName, "VPS Tunnel Service", 0xF01FF, 0x10, 0x3, 0x1, imagePath, null, IntPtr.Zero, null, "LocalSystem", null));
            if (created.IsInvalid) Throw("Не удалось создать службу.");
            SetServiceAcl(created.Handle, sid);
        }
        else
        {
            if (!ChangeServiceConfig(existing.Handle, 0xFFFFFFFF, 0x3, 0xFFFFFFFF, imagePath, null, IntPtr.Zero, null, "LocalSystem", null, "VPS Tunnel Service")) Throw("Не удалось обновить службу.");
            SetServiceAcl(existing.Handle, sid);
        }
    }

    private static ServiceHandle OpenScm(uint access)
    {
        var scm = new ServiceHandle(OpenSCManager(null, null, access));
        if (scm.IsInvalid) Throw("Нет доступа к диспетчеру служб.");
        return scm;
    }

    /// <summary>Closes VPS Tunnel windows started from the installation folder (they keep files locked).</summary>
    private static void CloseRunningGui()
    {
        foreach (var process in Process.GetProcessesByName("VPS-Tunnel"))
        {
            using (process)
            {
                try
                {
                    if (!string.Equals(process.MainModule?.FileName, GuiExe, StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
        }
    }

    /// <summary>An elevated installer would start the GUI elevated; Explorer starts it as the normal user.</summary>
    private static void LaunchUnelevated(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false })?.Dispose();

    private static bool DesktopRuntimeInstalled()
    {
        var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        return Directory.Exists(shared) && Directory.EnumerateDirectories(shared, "10.*").Any();
    }

    private static bool RunsFromAppDirectory() =>
        Environment.ProcessPath?.StartsWith(AppDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true;

    private const string TemporaryUninstallerPrefix = "VPS-Tunnel-Uninstall-";

    private static void RelaunchFromTemp()
    {
        var copy = Path.Combine(Path.GetTempPath(), TemporaryUninstallerPrefix + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Environment.ProcessPath!, copy);
        // Started directly by this elevated process, so the copy is elevated too (no second UAC prompt).
        using var _ = Process.Start(new ProcessStartInfo(copy, "--uninstall") { UseShellExecute = false })
            ?? throw new InvalidOperationException("Не удалось запустить удаление.");
    }

    private static void ScheduleSelfDeleteIfTemporary()
    {
        var path = Environment.ProcessPath;
        if (path != null && Path.GetFileName(path).StartsWith(TemporaryUninstallerPrefix, StringComparison.Ordinal))
            _ = MoveFileEx(path, null, 0x4); // MOVEFILE_DELAY_UNTIL_REBOOT
    }

    private static void ExtractPayload(string prefix, string destinationRoot)
    {
        var assembly = typeof(Program).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var relative = name[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!destination.StartsWith(Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Некорректный путь в пакете.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Повреждён пакет установки.");
            using var output = File.Create(destination);
            input.CopyTo(output);
        }
    }

    private static void CopySelf()
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден Setup EXE.");
        if (!string.Equals(Path.GetFullPath(source), SetupExe, StringComparison.OrdinalIgnoreCase)) File.Copy(source, SetupExe, true);
        ProtectFile(SetupExe, true);
    }

    private static void RegisterUninstaller()
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey, true);
        key.SetValue("DisplayName", ProductName);
        key.SetValue("DisplayVersion", ProductVersion);
        key.SetValue("Publisher", ProductName);
        key.SetValue("InstallLocation", AppDirectory);
        key.SetValue("UninstallString", '"' + SetupExe + '"' + " --uninstall");
        key.SetValue("DisplayIcon", GuiExe);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        var sizeKb = new[] { AppDirectory, EngineVersionDirectory }.Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Sum(file => new FileInfo(file).Length) / 1024;
        key.SetValue("EstimatedSize", (int)Math.Min(sizeKb, int.MaxValue), RegistryValueKind.DWord);
    }

    private static void CreateShortcut(string path)
    {
        var shell = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Не удалось создать ярлык.");
        dynamic wsh = Activator.CreateInstance(shell)!;
        try
        {
            dynamic shortcut = wsh.CreateShortcut(path);
            shortcut.TargetPath = GuiExe;
            shortcut.WorkingDirectory = AppDirectory;
            shortcut.IconLocation = GuiExe + ",0";
            shortcut.Description = "VPS Tunnel";
            shortcut.Save();
        }
        finally { Marshal.FinalReleaseComObject(wsh); }
    }

    private static void RemoveShortcuts()
    {
        // Current locations, the v1.0 Start menu folder, and per-user shortcuts made by hand.
        foreach (var path in new[]
                 {
                     StartMenuShortcut, DesktopShortcut,
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName)
                 })
            if (File.Exists(path)) File.Delete(path);
        var legacyFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductName);
        if (Directory.Exists(legacyFolder)) Directory.Delete(legacyFolder, true);
    }

    private static void RemoveAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ProductName) is string value && value.Contains(GuiExe, StringComparison.OrdinalIgnoreCase))
            key.DeleteValue(ProductName, throwOnMissingValue: false);
    }

    private static void DeleteManagedDirectory(string path)
    {
        // The uninstaller that started this copy may still be exiting from inside this folder.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                return;
            }
            catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
        }
    }

    private static void DeleteEngine(bool removeConfiguration)
    {
        // Do not remove unrelated files a user may have kept in C:\sing-box.
        if (Directory.Exists(EngineVersionDirectory)) Directory.Delete(EngineVersionDirectory, true);
        if (removeConfiguration)
            foreach (var name in new[] { "config.json", "config.json.bak", "config.json.new" })
            {
                var path = Path.Combine(EngineDirectory, name);
                if (File.Exists(path)) File.Delete(path);
            }
        if (Directory.Exists(EngineDirectory) && !Directory.EnumerateFileSystemEntries(EngineDirectory).Any())
            Directory.Delete(EngineDirectory);
    }

    private static void ProtectDirectory(string path, bool allowUsersRead)
    {
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (allowUsersRead) security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    private static void ProtectTree(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            ProtectFile(path, path.StartsWith(AppDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(EngineVersionDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
    private static void ProtectFile(string path, bool allowUsersRead)
    {
        var security = new FileSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        if (allowUsersRead) security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static bool ServicePointsToExpectedLocation()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
        return string.Equals(key?.GetValue("ImagePath") as string, '"' + ServiceExe + '"', StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsStopped(IntPtr service) { if (!QueryServiceStatus(service, out var status)) Throw("Не удалось прочитать состояние службы."); return status.CurrentState == 1; }
    private static void StopIfRunning(IntPtr service)
    {
        if (IsStopped(service)) return;
        if (!ControlService(service, 1, out _) && Marshal.GetLastWin32Error() != 1062) Throw("Не удалось остановить службу.");
        var until = DateTime.UtcNow.AddSeconds(40); while (DateTime.UtcNow < until) { if (IsStopped(service)) return; Thread.Sleep(250); }
        throw new TimeoutException("Служба не остановилась за 40 секунд.");
    }
    private static void SetServiceAcl(IntPtr service, SecurityIdentifier user)
    {
        var sddl = $"D:P(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;LCRPWP;;;{user.Value})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _)) Throw("Не удалось сформировать ACL службы.");
        try { if (!SetServiceObjectSecurity(service, 4, descriptor)) Throw("Не удалось назначить ACL службы."); } finally { LocalFree(descriptor); }
    }
    private static void Throw(string message) => throw new Win32Exception(Marshal.GetLastWin32Error(), message);
    private sealed class ServiceHandle(IntPtr handle) : IDisposable { public IntPtr Handle { get; } = handle; public bool IsInvalid => Handle == IntPtr.Zero; public void Dispose() { if (!IsInvalid) CloseServiceHandle(Handle); } }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateService(IntPtr scm, string name, string displayName, uint access, uint type, uint start, uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password, string? displayName);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DeleteService(IntPtr service);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceObjectSecurity(IntPtr service, uint info, IntPtr descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool MoveFileEx(string existing, string? replacement, uint flags);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CloseServiceHandle(IntPtr service);
}
