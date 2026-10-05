using System.IO;
using System.Security;
using System.Windows;
using Microsoft.Win32;
using VPS.Tunnel.Core;

namespace VPS.Tunnel.App;

/// <summary>Replaces complete resource dictionaries; never mutates frozen WPF brushes.</summary>
internal sealed class ThemeService : IDisposable
{
    private ThemePreference _preference;
    public event EventHandler? Changed;

    public ThemeService() => SystemEvents.UserPreferenceChanged += OnWindowsPreferenceChanged;

    public void Apply(ThemePreference preference)
    {
        _preference = preference;
        var dark = preference == ThemePreference.Dark ||
            preference == ThemePreference.System && IsWindowsDark();
        var dictionary = new ResourceDictionary {
            Source = new Uri($"/VPS-Tunnel;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        Application.Current.Resources.MergedDictionaries[0] = dictionary;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnWindowsPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_preference != ThemePreference.System) return;
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(() => { if (_preference == ThemePreference.System) Apply(_preference); });
    }

    private static bool IsWindowsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        // A restricted registry falls back to Light. XAML/resource errors are not swallowed.
        catch (SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnWindowsPreferenceChanged;
}
