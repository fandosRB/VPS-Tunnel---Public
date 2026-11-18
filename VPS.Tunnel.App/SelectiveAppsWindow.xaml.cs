using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using VPS.Tunnel.Core;

namespace VPS.Tunnel.App;

/// <summary>
/// Edits a program list: SELECTIVE (executable names that go through the VPS, plus WSL) or
/// EXCLUDE (full paths, or names, of programs that bypass the tunnel).
/// </summary>
public partial class SelectiveAppsWindow : Window
{
    private readonly ObservableCollection<string> _applications;
    private readonly bool _exclude;

    public SelectiveAppsWindow(SelectiveRequest current) : this(SelectiveApplications.Normalize(current.Applications), exclude: false)
    {
        WslCheck.IsChecked = current.IncludeWsl;
    }

    public SelectiveAppsWindow(IReadOnlyList<string> excluded) : this(ExcludedApplications.Normalize(excluded), exclude: true) { }

    private SelectiveAppsWindow(List<string> entries, bool exclude)
    {
        InitializeComponent();
        _exclude = exclude;
        _applications = new(entries);
        _applications.CollectionChanged += (_, _) => UpdateEmptyHint();
        AppList.ItemsSource = _applications;
        if (exclude)
        {
            Title = HeaderText.Text = "Исключения: программы напрямую";
            DescriptionText.Text = "Весь трафик идёт через VPS, а эти программы — напрямую, мимо туннеля. " +
                "Указывайте полный путь к .exe: у разных программ бывают одинаковые имена файлов. " +
                "Программы, которые запускает выбранная, нужно добавить отдельно.";
            WslCheck.Visibility = Visibility.Collapsed;
            NameHint.Text = "Полный путь к .exe";
            NameBox.ToolTip = "Полный путь к .exe или имя файла";
        }
        UpdateEmptyHint();
    }

    public SelectiveRequest Result => new(_applications.ToList(), WslCheck.IsChecked == true);
    public IReadOnlyList<string> ExcludedEntries => _applications.ToList();

    private void Add_Click(object sender, RoutedEventArgs e) => AddFromBox();

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddFromBox();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) =>
        NameHint.Visibility = NameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void AddFromBox()
    {
        if (TryAdd(NameBox.Text)) NameBox.Clear();
        NameBox.Focus();
    }

    private bool TryAdd(string? input)
    {
        var text = input?.Trim().Trim('"').Trim() ?? "";
        // SELECTIVE matches by file name; EXCLUDE keeps the full path when one is given.
        var entry = _exclude && text.Contains('\\') ? text : Path.GetFileName(text);
        if (entry.Length > 0 && !entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) entry += ".exe";
        var valid = _exclude ? ExcludedApplications.IsValidEntry(entry) : SelectiveApplications.IsValidName(entry);
        if (!valid)
            return ShowError(_exclude
                ? "Укажите полный путь к .exe или имя файла."
                : "Укажите имя файла программы, например chrome.exe.");
        if (_applications.Contains(entry, StringComparer.OrdinalIgnoreCase))
            return ShowError(entry + " уже в списке.");
        var max = _exclude ? ExcludedApplications.MaxCount : SelectiveApplications.MaxCount;
        if (_applications.Count >= max)
            return ShowError($"Можно добавить не больше {max} программ.");
        _applications.Add(entry);
        if (_exclude && ExcludedApplications.IsPath(entry) && !File.Exists(entry))
            ShowError("Файл не найден: " + entry + ". Пока его нет, при включении режима он будет пропущен.");
        else
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
        var picker = new AppPickerWindow(running, _applications, usePaths: _exclude) { Owner = this };
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
