using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Security.Principal;

namespace VPS.Tunnel.App.Services;

public sealed class WindowsStartupService
{
    private const string ValueName = "VPS Tunnel";
    private readonly IStartupValueStore _store;
    private readonly Func<string> _productionExecutable;

    public WindowsStartupService(IStartupValueStore? store = null, Func<string>? productionExecutable = null)
    {
        _store = store ?? new RegistryStartupValueStore();
        _productionExecutable = productionExecutable ?? ResolveProductionExecutable;
    }

    public static string ResolveProductionExecutable()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "VPS Tunnel", "VPS-Tunnel.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("Установленный VPS-Tunnel.exe не найден.", path);
        return path;
    }

    public void Apply(bool enabled, string? executableOverride = null)
    {
        if (enabled)
        {
            var path = executableOverride ?? _productionExecutable();
            path = Path.GetFullPath(path);
            if (!string.Equals(Path.GetFileName(path), "VPS-Tunnel.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Автозапуск доступен только для VPS-Tunnel.exe.");
            _store.Set(ValueName, '"' + path + '"' + " --autostart");
        }
        else _store.Delete(ValueName);
    }
}

public interface IStartupValueStore
{
    void Set(string name, string value);
    void Delete(string name);
}

public sealed class RegistryStartupValueStore : IStartupValueStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public void Set(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Windows Run key недоступен.");
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

public sealed class SetupLauncher
{
    /// <summary>Re-registers the service for this user with the installed setup (one UAC prompt).</summary>
    public Process Launch()
    {
        var setup = Path.Combine(AppContext.BaseDirectory, "VPS-Tunnel-Setup.exe");
        if (!File.Exists(setup)) throw new FileNotFoundException("Установщик не найден. Запустите VPS-Tunnel-Setup.exe ещё раз.");
        var info = new ProcessStartInfo(setup)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        info.ArgumentList.Add("--repair-service");
        return Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить установщик.");
    }
}
