namespace VPS.Tunnel.App.Services;

public interface IStartupWindow
{
    bool ShouldStartHidden { get; }
    void ConfigureHiddenStartup();
    bool InitializeTrayWithoutShowing();
    void ShowNormally();
}

public interface ITrayWindow
{
    void SetNormalState();
    void SetTaskbarVisible(bool visible);
    void ShowWindow();
    void ActivateWindow();
    void HideWindow();
}

public static class WindowStartupPresenter
{
    public static void Present(IStartupWindow window)
    {
        if (window.ShouldStartHidden)
        {
            window.ConfigureHiddenStartup();
            if (window.InitializeTrayWithoutShowing()) return;
        }
        window.ShowNormally();
    }

    public static void Restore(ITrayWindow window)
    {
        window.SetNormalState();
        window.SetTaskbarVisible(true);
        window.ShowWindow();
        window.ActivateWindow();
    }

    public static void HideToTray(ITrayWindow window)
    {
        window.HideWindow();
        window.SetTaskbarVisible(false);
    }
}
