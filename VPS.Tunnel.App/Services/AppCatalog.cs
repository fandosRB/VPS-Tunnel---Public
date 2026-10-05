using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VPS.Tunnel.Core;

namespace VPS.Tunnel.App.Services;

public sealed class AppChoice(string displayName, string executableName, ImageSource? icon)
{
    public string DisplayName { get; } = displayName;
    public string ExecutableName { get; } = executableName;
    public ImageSource? Icon { get; } = icon;
    public bool IsSelected { get; set; }
}

/// <summary>Programs the user can pick for SELECTIVE: installed (Start menu, desktop) or running.</summary>
public static class AppCatalog
{
    private static readonly string[] UninstallerMarkers = ["uninstall", "unins0", "удал"];

    /// <summary>
    /// Installed programs: Start menu and desktop shortcuts to an .exe, plus packaged
    /// (Microsoft Store / MSIX) apps such as ChatGPT or Claude. Call off the UI thread.
    /// </summary>
    public static List<AppChoice> Installed() => RunOnStaThread(() =>
    {
        var result = new List<AppChoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string displayName, string executablePath)
        {
            var executable = Path.GetFileName(executablePath);
            if (IsUninstaller(displayName, executablePath) || !SelectiveApplications.IsValidName(executable) ||
                !seen.Add(executable)) return;
            result.Add(new AppChoice(displayName, executable, LoadIcon(executablePath)));
        }
        foreach (var (name, path) in ShortcutApps()) Add(name, path);
        foreach (var (name, path) in PackagedApps()) Add(name, path);
        return result.OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    private static List<(string Name, string Path)> ShortcutApps()
    {
        var result = new List<(string, string)>();
        var shell = Type.GetTypeFromProgID("WScript.Shell");
        if (shell == null) return result;
        dynamic wsh = Activator.CreateInstance(shell)!;
        try
        {
            var shortcuts = new[]
                {
                    (Environment.SpecialFolder.CommonStartMenu, SearchOption.AllDirectories),
                    (Environment.SpecialFolder.StartMenu, SearchOption.AllDirectories),
                    (Environment.SpecialFolder.CommonDesktopDirectory, SearchOption.TopDirectoryOnly),
                    (Environment.SpecialFolder.DesktopDirectory, SearchOption.TopDirectoryOnly)
                }
                .SelectMany(location => Shortcuts(Environment.GetFolderPath(location.Item1), location.Item2));
            foreach (var shortcut in shortcuts)
            {
                string target, arguments;
                try
                {
                    var link = wsh.CreateShortcut(shortcut);
                    target = (string)link.TargetPath;
                    arguments = (string)link.Arguments;
                }
                catch (COMException) { continue; }
                if (string.IsNullOrEmpty(target) || !File.Exists(target)) continue;
                result.Add((Path.GetFileNameWithoutExtension(shortcut), SquirrelTarget(target, arguments) ?? target));
            }
        }
        finally { Marshal.FinalReleaseComObject(wsh); }
        return result;
    }

    /// <summary>
    /// Squirrel-installed apps (Discord, older Claude, ...) start through "Update.exe --processStart app.exe";
    /// the process that actually connects is app.exe next to Update.exe.
    /// </summary>
    private static string? SquirrelTarget(string target, string arguments)
    {
        if (!Path.GetFileName(target).Equals("Update.exe", StringComparison.OrdinalIgnoreCase)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(arguments,
            @"--processStart(?:=|\s+)""?(?<exe>[^""\s]+\.exe)""?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? Path.Combine(Path.GetDirectoryName(target)!, match.Groups["exe"].Value) : null;
    }

    /// <summary>Packaged apps from the Windows "Applications" folder, resolved to the executable in their manifest.</summary>
    private static List<(string Name, string Path)> PackagedApps()
    {
        var result = new List<(string, string)>();
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return result;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            var folder = shell.NameSpace("shell:AppsFolder");
            if (folder == null) return result;
            var items = folder.Items();
            int count = items.Count;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    var item = items.Item(i);
                    string name = item.Name;
                    string id = item.Path; // AppUserModelID for packaged apps: Family_PublisherId!AppId
                    if (PackagedExecutable(id) is { } executable) result.Add((name, executable));
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or
                    Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            }
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally { Marshal.FinalReleaseComObject(shell); }
        return result;
    }

    private const string PackageRepository =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private static string? PackagedExecutable(string appUserModelId)
    {
        var bang = appUserModelId.IndexOf('!');
        if (bang <= 0) return null;
        var family = appUserModelId[..bang];
        var appId = appUserModelId[(bang + 1)..];
        var separator = family.LastIndexOf('_');
        if (separator <= 0 || family.Contains('\\')) return null;
        var packageName = family[..separator];
        var publisherId = family[(separator + 1)..];

        using var packages = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PackageRepository);
        if (packages == null) return null;
        // Full name: Name_Version_Architecture_ResourceId_PublisherId. Prefer the newest installed version.
        foreach (var fullName in packages.GetSubKeyNames()
                     .Where(key => key.StartsWith(packageName + "_", StringComparison.OrdinalIgnoreCase) &&
                         key.EndsWith("_" + publisherId, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(key => key, StringComparer.OrdinalIgnoreCase))
        {
            using var package = packages.OpenSubKey(fullName);
            if (package?.GetValue("PackageRootFolder") is not string root) continue;
            try
            {
                var manifest = System.Xml.Linq.XDocument.Load(Path.Combine(root, "AppxManifest.xml"));
                var executable = manifest.Descendants()
                    .Where(element => element.Name.LocalName == "Application" && (string?)element.Attribute("Id") == appId)
                    .Select(element => (string?)element.Attribute("Executable"))
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(executable) && executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(root, executable);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { }
        }
        return null;
    }

    /// <summary>Shell COM objects (Shell.Application, WScript.Shell) expect a single-threaded apartment.</summary>
    private static T RunOnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new InvalidOperationException("Application list failed.", error);
        return result;
    }

    /// <summary>Running programs that have a window. Call off the UI thread.</summary>
    public static List<AppChoice> Running()
    {
        var result = new List<AppChoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero) continue;
                    var executable = process.ProcessName + ".exe";
                    if (!SelectiveApplications.IsValidName(executable) || !seen.Add(executable)) continue;
                    string? path = null;
                    string? description = null;
                    try
                    {
                        path = process.MainModule?.FileName;
                        description = process.MainModule?.FileVersionInfo.FileDescription;
                    }
                    catch (Win32Exception) { } // elevated or protected process: name only
                    result.Add(new AppChoice(string.IsNullOrWhiteSpace(description) ? process.ProcessName : description.Trim(),
                        executable, path == null ? null : LoadIcon(path)));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
        }
        return result.OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IEnumerable<string> Shortcuts(string folder, SearchOption depth)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return [];
        try
        {
            return Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions
            {
                RecurseSubdirectories = depth == SearchOption.AllDirectories,
                IgnoreInaccessible = true
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool IsUninstaller(string displayName, string target) =>
        UninstallerMarkers.Any(marker =>
            displayName.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(target).Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static ImageSource? LoadIcon(string path)
    {
        var info = new ShFileInfo();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon) == IntPtr.Zero ||
            info.Icon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); // created on a worker thread, shown on the UI thread
            return image;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException) { return null; }
        finally { DestroyIcon(info.Icon); }
    }

    private const uint ShgfiIcon = 0x100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
