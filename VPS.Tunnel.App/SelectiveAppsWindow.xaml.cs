using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using VPS.Tunnel.Core;

namespace VPS.Tunnel.App;

public partial class SelectiveAppsWindow : Window
{
    private readonly ObservableCollection<string> _applications;

    public SelectiveAppsWindow(SelectiveRequest current)
    {
        InitializeComponent();
        _applications = new(SelectiveApplications.Normalize(current.Applications));
        WslCheck.IsChecked = current.IncludeWsl;
        _applications.CollectionChanged += (_, _) => UpdateEmptyHint();
        AppList.ItemsSource = _applications;
        UpdateEmptyHint();
    }

    public SelectiveRequest Result => new(_applications.ToList(), WslCheck.IsChecked == true);

    private void Add_Click(object sender, RoutedEventArgs e) => AddFromBox();

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddFromBox();
    }

    private void AddFromBox()
    {
        if (TryAdd(NameBox.Text)) NameBox.Clear();
        NameBox.Focus();
    }

    private bool TryAdd(string? input)
    {
        var name = Path.GetFileName(input?.Trim().Trim('"') ?? "");
        if (name.Length > 0 && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        if (!SelectiveApplications.IsValidName(name))
            return ShowError("Укажите имя файла программы, например chrome.exe.");
        if (_applications.Contains(name, StringComparer.OrdinalIgnoreCase))
            return ShowError(name + " уже в списке.");
        if (_applications.Count >= SelectiveApplications.MaxCount)
            return ShowError($"Можно добавить не больше {SelectiveApplications.MaxCount} программ.");
        _applications.Add(name);
        ErrorText.Visibility = Visibility.Collapsed;
        return true;
    }

    private bool ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        return false;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name }) _applications.Remove(name);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите программу",
            Filter = "Программы (*.exe)|*.exe",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) TryAdd(file);
    }

    private void Installed_Click(object sender, RoutedEventArgs e) => Pick(running: false);

    private void Running_Click(object sender, RoutedEventArgs e) => Pick(running: true);

    private void Pick(bool running)
    {
        var picker = new AppPickerWindow(running, _applications) { Owner = this };
        if (picker.ShowDialog() != true) return;
        foreach (var name in picker.Selected) TryAdd(name);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Text typed but not yet added is almost always meant to be included.
        if (!string.IsNullOrWhiteSpace(NameBox.Text) && !TryAdd(NameBox.Text)) return;
        DialogResult = true;
    }

    private void UpdateEmptyHint() =>
        EmptyHint.Visibility = _applications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}
