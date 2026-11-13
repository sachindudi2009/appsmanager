using System.Windows;
using SecretChat.Desktop.Services;

namespace SecretChat.Desktop;

public partial class App : System.Windows.Application
{
    private MainWindow? _window;
    private TrayController? _tray;
    private StartupService? _startup;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _startup = new StartupService();
        _tray = new TrayController();
        _window = new MainWindow(_tray);
        _tray.ChatRequested += (_, _) => _window.OpenChat();
        _tray.SettingsChanged += (_, _) => _window.ApplySettings();
        _window.NotificationRequested += (_, item) => ShowNotification(item.Title, item.Body, item.Call);
        _window.HideToTrayRequested += (_, _) => HideWindow();
        _window.Show();
    }

    private void ShowWindow() { if (_window is null) return; _window.Show(); _window.WindowState = WindowState.Normal; _window.Activate(); }
    private void HideWindow() { _window?.Hide(); }
    private void ShowNotification(string title, string body, bool call)
    {
        if (_tray?.Settings.Notifications != true || _window?.IsChatOpen == true) return;
        if (_tray.Settings.StudyTips) body = new[] { "Take one slow breath, then continue with the next small step.", "Try a short focus sprint and a quick stretch break.", "Recall one idea from memory before checking your notes.", "Explain the last thing you learned in one clear sentence." }[Random.Shared.Next(4)];
        if (_tray.Settings.Sound) System.Media.SystemSounds.Asterisk.Play();
        var popup = new NotificationWindow(title, body, call);
        popup.OpenChatRequested += (_, _) => _window.OpenChat(); popup.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
