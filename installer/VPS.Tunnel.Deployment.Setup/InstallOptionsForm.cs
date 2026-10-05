namespace VPS.Tunnel.Deployment.Setup;

/// <summary>The only installer page: where to put shortcuts and whether to start the app afterwards.</summary>
internal sealed class InstallOptionsForm : Form
{
    private readonly CheckBox _startMenu = new() { Text = "Ярлык в меню «Пуск»", AutoSize = true };
    private readonly CheckBox _desktop = new() { Text = "Ярлык на рабочем столе", AutoSize = true };
    private readonly CheckBox _launch = new() { Text = "Запустить VPS Tunnel после установки", AutoSize = true, Checked = true };

    public InstallOptionsForm(bool update, bool startMenu, bool desktop)
    {
        Text = Program.ProductName + " " + Program.ProductVersion;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(16);
        _startMenu.Checked = startMenu;
        _desktop.Checked = desktop;

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false
        };
        layout.Controls.Add(new Label
        {
            Text = update ? "Обновление VPS Tunnel до версии " + Program.ProductVersion : "Установка VPS Tunnel " + Program.ProductVersion,
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        });
        layout.Controls.Add(new Label
        {
            Text = update
                ? "Программа будет закрыта, а туннель отключён на время обновления.\nНастройки подключения и список программ сохранятся."
                : "Программа будет установлена в «Program Files». После установки\nукажите свой сервер — ссылку vless:// из панели сервера.",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12)
        });
        foreach (var box in new[] { _startMenu, _desktop, _launch })
        {
            box.Margin = new Padding(0, 2, 0, 2);
            layout.Controls.Add(box);
        }

        var install = new Button { Text = update ? "Обновить" : "Установить", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(100, 30) };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(100, 30) };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 16, 0, 0)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(install);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = install;
        CancelButton = cancel;
    }

    public bool StartMenu => _startMenu.Checked;
    public bool Desktop => _desktop.Checked;
    public bool Launch => _launch.Checked;
}
