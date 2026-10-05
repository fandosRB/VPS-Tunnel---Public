using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using VPS.Tunnel.App.Services;

namespace VPS.Tunnel.App;

public partial class AppPickerWindow : Window
{
    private readonly HashSet<string> _alreadyAdded;
    private ICollectionView? _view;

    /// <param name="running">true: running programs with a window; false: installed programs.</param>
    public AppPickerWindow(bool running, IEnumerable<string> alreadyAdded)
    {
        InitializeComponent();
        _alreadyAdded = new(alreadyAdded, StringComparer.OrdinalIgnoreCase);
        HeaderText.Text = running ? "Запущенные программы" : "Установленные программы";
        Title = HeaderText.Text;
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            try
            {
                var apps = await Task.Run(() => running ? AppCatalog.Running() : AppCatalog.Installed());
                apps.RemoveAll(app => _alreadyAdded.Contains(app.ExecutableName));
                AppList.ItemsSource = apps;
                _view = CollectionViewSource.GetDefaultView(apps);
                _view.Filter = Matches;
                UpdateStatus();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                StatusText.Text = "Не удалось получить список программ. Добавьте программу через «Выбрать файл…».";
            }
        };
    }

    public IReadOnlyList<string> Selected { get; private set; } = [];

    private bool Matches(object item)
    {
        var query = SearchBox.Text.Trim();
        return query.Length == 0 || item is AppChoice app &&
            (app.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             app.ExecutableName.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view?.Refresh();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_view == null) return;
        var empty = _view.IsEmpty;
        StatusText.Text = SearchBox.Text.Trim().Length > 0 ? "Ничего не найдено." : "Нет программ для добавления.";
        StatusText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Selected = (AppList.ItemsSource as IEnumerable<AppChoice> ?? [])
            .Where(app => app.IsSelected).Select(app => app.ExecutableName).ToList();
        DialogResult = true;
    }
}
