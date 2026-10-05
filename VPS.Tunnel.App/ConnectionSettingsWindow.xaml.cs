using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VPS.Tunnel.Core;

namespace VPS.Tunnel.App;

/// <param name="DisplayName">Name shown in the main window.</param>
/// <param name="Configuration">New sing-box configuration, or null to change only the name.</param>
/// <param name="ServerHost">Server address of a profile (for the expected exit IP); null for an imported file.</param>
public sealed record ConnectionSettingsResult(string DisplayName, string? Configuration, string? ServerHost);

public partial class ConnectionSettingsWindow : Window
{
    private readonly bool _configured;
    private string? _imported;

    public ConnectionSettingsWindow(string? currentName, bool configured, bool welcome)
    {
        InitializeComponent();
        _configured = configured;
        NameBox.Text = currentName ?? "";
        foreach (var fingerprint in ConnectionProfile.Fingerprints) FingerprintBox.Items.Add(fingerprint);
        FingerprintBox.SelectedIndex = 0;
        FlowBox.SelectedIndex = 0;
        WelcomeText.Visibility = welcome ? Visibility.Visible : Visibility.Collapsed;
        KeepHint.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => LinkBox.Focus();
    }

    public ConnectionSettingsResult? Result { get; private set; }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { if (Clipboard.ContainsText()) LinkBox.Text = Clipboard.GetText().Trim(); }
        catch (System.Runtime.InteropServices.COMException) { } // clipboard busy
    }

    private void LinkBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = LinkBox.Text.Trim();
        if (text.Length == 0) { LinkStatus.Visibility = Visibility.Collapsed; return; }
        try
        {
            var profile = VlessLink.Parse(text);
            Fill(profile);
            ShowLinkStatus("Ссылка распознана: " + profile.Server + ":" + profile.Port, error: false);
        }
        catch (FormatException ex) { ShowLinkStatus(ex.Message, error: true); }
    }

    private void ShowLinkStatus(string text, bool error)
    {
        LinkStatus.Text = text;
        LinkStatus.Foreground = error ? System.Windows.Media.Brushes.IndianRed : (System.Windows.Media.Brush)FindResource("AccentBrush");
        LinkStatus.Visibility = Visibility.Visible;
    }

    private void Fill(ConnectionProfile profile)
    {
        NameBox.Text = profile.Name;
        ServerBox.Text = profile.Server;
        PortBox.Text = profile.Port.ToString();
        UuidBox.Text = profile.Uuid;
        SniBox.Text = profile.ServerName;
        PublicKeyBox.Text = profile.PublicKey;
        ShortIdBox.Text = profile.ShortId;
        FingerprintBox.SelectedItem = profile.Fingerprint;
        FlowBox.SelectedIndex = profile.Flow.Length == 0 ? 1 : 0;
        ClearImport();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Конфигурация sing-box", Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        string json;
        try { json = File.ReadAllText(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Не удалось прочитать файл. Скопируйте его в папку Документы и выберите оттуда.");
            return;
        }
        if (SingBoxConfigTemplate.ValidateImported(json) is { } error) { ShowError(error); return; }
        _imported = json;
        ImportStatus.Text = "Будет использован файл: " + dialog.FileName + ". Поля выше при сохранении не учитываются.";
        ImportStatus.Visibility = Visibility.Visible;
        if (NameBox.Text.Trim().Length == 0) NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void ClearImport()
    {
        _imported = null;
        ImportStatus.Visibility = Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (_imported != null)
        {
            Result = new(name.Length == 0 ? "Свой config.json" : name, _imported, null);
            DialogResult = true;
            return;
        }

        var connectionFields = new[] { ServerBox, UuidBox, SniBox, PublicKeyBox, ShortIdBox };
        if (connectionFields.All(box => box.Text.Trim().Length == 0))
        {
            if (!_configured) { ShowError("Вставьте ссылку vless:// или заполните поля подключения."); return; }
            Result = new(name, null, null); // only the display name changes
            DialogResult = true;
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port)) { ShowError("Порт должен быть числом от 1 до 65535."); return; }
        var profile = new ConnectionProfile(
            Name: name.Length == 0 ? ServerBox.Text.Trim() : name,
            Server: ServerBox.Text.Trim(),
            Port: port,
            Uuid: UuidBox.Text.Trim(),
            ServerName: SniBox.Text.Trim(),
            PublicKey: PublicKeyBox.Text.Trim(),
            ShortId: ShortIdBox.Text.Trim(),
            Fingerprint: FingerprintBox.SelectedItem as string ?? "chrome",
            Flow: FlowBox.SelectedIndex == 0 ? "xtls-rprx-vision" : "");
        if (profile.Validate() is { } error) { ShowError(error); return; }
        Result = new(profile.Name, SingBoxConfigTemplate.Build(profile), profile.Server);
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
