using System.ComponentModel;
using System.Windows;
using Marsianskiy.ViewModels;
using Marsianskiy.Services;
using WinForms = System.Windows.Forms;

namespace Marsianskiy;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private WinForms.NotifyIcon? _trayIcon;
    private bool _shutdownCompleted;
    private bool _forceShutdown;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) =>
        {
            try
            {
                _viewModel.Initialize();
                InitializeTray();
                await _viewModel.RecoverNetworkAsync();
                if (_viewModel.AutoReconnect && _viewModel.Servers.Count > 0 && _viewModel.Services.Any(x => x.Enabled))
                    await _viewModel.ConnectOrDisconnectAsync();
            }
            catch (Exception ex)
            {
                var logPath = Path.Combine(AppPaths.DataDirectory, "startup-error.log");
                File.WriteAllText(logPath, SecretRedactor.Redact(ex.ToString()));
                FileSecurity.RestrictFile(logPath);
                MessageBox.Show(this, ex.Message, "marsianskiy — ошибка инициализации", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        Closed += (_, _) => _trayIcon?.Dispose();
    }

    private void InitializeTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Открыть marsianskiy", null, (_, _) => ShowFromTray());
        menu.Items.Add("Подключить / отключить VPN", null, (_, _) => _viewModel.ConnectCommand.Execute(null));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => { _forceShutdown = true; Close(); });
        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "marsianskiy",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Path.Combine(AppContext.BaseDirectory, "marsianskiy.exe")),
            Visible = _viewModel.CloseToTray,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CloseToTray)) _trayIcon.Visible = _viewModel.CloseToTray;
        };
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void WindowMaximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    private async void ImportUrl_Click(object sender, RoutedEventArgs e)
    {
        try { await _viewModel.ImportUrlAsync(SubscriptionUrlBox.Password); }
        finally { SubscriptionUrlBox.Clear(); }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.CloseToTray && !_forceShutdown && !_shutdownCompleted)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Hide();
                _trayIcon?.ShowBalloonTip(1400, "marsianskiy", "Клиент продолжает работать в трее. Для полного выхода используйте меню значка.", WinForms.ToolTipIcon.Info);
            }));
            return;
        }
        if (_shutdownCompleted)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        IsEnabled = false;
        try { await _viewModel.ShutdownAsync(); }
        finally
        {
            _shutdownCompleted = true;
            System.Windows.Application.Current.Shutdown();
        }
    }
}
