using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using SecretChat.Desktop.Services;

namespace SecretChat.Desktop;

public partial class MainWindow : Window
{
    private readonly TrayController _tray;
    private readonly CredentialStore _credentials = new();
    private bool _webReady;
    private bool _chatOpen;
    private TaskCompletionSource<bool>? _loginResult;
    private readonly string _webRoot = Path.Combine(AppContext.BaseDirectory, "Web");
    public event EventHandler? HideToTrayRequested;
    public event EventHandler<(string Title, string Body, bool Call)>? NotificationRequested;

    public MainWindow(TrayController tray)
    {
        InitializeComponent();
        _tray = tray;
        Loaded += async (_, _) => await InitializeBrowserAsync();
    }

    public bool IsChatOpen => _chatOpen;

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecretChat", "WebView2");
            Directory.CreateDirectory(profile);
            var env = await CoreWebView2Environment.CreateAsync(null, profile);
            await Browser.EnsureCoreWebView2Async(env);
            var core = Browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.AreDevToolsEnabled = false; core.Settings.IsStatusBarEnabled = false; core.Settings.IsZoomControlEnabled = false;
            core.PermissionRequested += (_, e) => { if (e.PermissionKind is CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Camera) e.State = CoreWebView2PermissionState.Allow; };
            core.WebMessageReceived += OnWebMessage;
            core.SetVirtualHostNameToFolderMapping("secretchat.local", _webRoot, CoreWebView2HostResourceAccessKind.Allow);
            core.NavigationCompleted += async (_, args) => { LoadingCard.Visibility = Visibility.Collapsed; if (!args.IsSuccess) return; _webReady = true; ApplySettings(); await TryAutoLoginAsync(); };
            Browser.Source = new Uri("https://secretchat.local/index.html");
        }
        catch (Exception ex) { System.Windows.MessageBox.Show($"Secret Chat could not start: {ex.Message}", "Secret Chat", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task TryAutoLoginAsync()
    {
        var saved = _credentials.Load();
        if (saved is null || string.IsNullOrWhiteSpace(saved.Username)) { ShowLogin(); return; }
        await LoginInWebViewAsync(saved.Username, saved.Password, false);
    }

    private async Task LoginInWebViewAsync(string username, string password, bool save)
    {
        LoginButton.IsEnabled = false; LoginError.Text = "";
        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new { username, password });
            _loginResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Browser.CoreWebView2.ExecuteScriptAsync($"window.SecretChatHost?.login({payload})");
            var result = await _loginResult.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (!result) throw new InvalidOperationException("The username or password was not accepted. Check the credentials and try again.");
            if (save) _credentials.Save(username, password);
            LoginCard.Visibility = Visibility.Collapsed;
            _chatOpen = true;
            Browser.CoreWebView2.PostWebMessageAsJson("{\"kind\":\"showChat\"}");
            ApplySettings();
            if (save) { _chatOpen = false; HideToTrayRequested?.Invoke(this, EventArgs.Empty); Browser.CoreWebView2.PostWebMessageAsJson("{\"kind\":\"hideChat\"}"); _ = ClearTransientWebContentAsync(); }

        }
        catch (Exception ex) { LoginError.Text = ex.Message; LoginCard.Visibility = Visibility.Visible; }
        finally { LoginButton.IsEnabled = true; }
    }

    private void ShowLogin() { LoginCard.Visibility = Visibility.Visible; UsernameInput.Focus(); }
    private async void Login_Click(object sender, RoutedEventArgs e) => await LoginInWebViewAsync(UsernameInput.Text.Trim(), PasswordInput.Password, true);

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;
            if (root.TryGetProperty("kind", out var kind) && kind.GetString() == "openChat") Dispatcher.Invoke(OpenChat);
            else if (root.TryGetProperty("kind", out kind) && kind.GetString() == "closePopup") Dispatcher.Invoke(CloseChatPopup);
            else if (root.TryGetProperty("kind", out kind) && kind.GetString() == "loginResult")
            {
                var ok = root.GetProperty("ok").GetBoolean();
                if (!ok && root.TryGetProperty("message", out var message)) Dispatcher.Invoke(() => LoginError.Text = message.GetString());
                _loginResult?.TrySetResult(ok);
            }
            else if (root.TryGetProperty("kind", out kind) && kind.GetString() == "notification")
            {
                var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "Secret Chat" : "Secret Chat";
                var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "New activity" : "New activity";
                var call = root.TryGetProperty("call", out var c) && c.GetBoolean();
                Dispatcher.Invoke(() => { if (!_chatOpen && _tray.Settings.Notifications) NotificationRequested?.Invoke(this, (title, body, call)); });
            }
        }
        catch { }
    }

    public void ApplySettings()
    {
        if (!_webReady) return;
        var json = System.Text.Json.JsonSerializer.Serialize(_tray.Settings);
        Browser.CoreWebView2.PostWebMessageAsJson($"{{\"kind\":\"settings\",\"settings\":{json}}}");
    }

    public void OpenChat()
    {
        if (_credentials.Load() is null) { Show(); Activate(); ShowLogin(); return; }
        Show(); WindowState = WindowState.Normal; _chatOpen = true;
        if (_webReady) Browser.CoreWebView2.PostWebMessageAsJson("{\"kind\":\"showChat\"}");
        Activate();
    }

    private void CloseChatPopup()
    {
        _chatOpen = false;
        if (_webReady) Browser.CoreWebView2.PostWebMessageAsJson("{\"kind\":\"hideChat\"}");
        HideToTrayRequested?.Invoke(this, EventArgs.Empty);
        _ = ClearTransientWebContentAsync();
    }

    private async Task ClearTransientWebContentAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (_chatOpen || !_webReady) return;
        try
        {
            await Browser.ExecuteScriptAsync("window.SecretChatHost?.suspendDocument()");
        }
        catch { }
    }

    private void Window_StateChanged(object? sender, EventArgs e) { if (WindowState == WindowState.Minimized) CloseChatPopup(); }
    private void Window_Closing(object? sender, CancelEventArgs e) { e.Cancel = true; CloseChatPopup(); }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else DragMove(); }
    private void Hide_Click(object sender, RoutedEventArgs e) => CloseChatPopup();
}


