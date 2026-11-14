using System.Windows;
using Marsianskiy.Services;

namespace Marsianskiy;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                AppPaths.EnsureDirectories();
                var logPath = Path.Combine(AppPaths.DataDirectory, "startup-error.log");
                File.WriteAllText(logPath, SecretRedactor.Redact(args.Exception.ToString()));
                FileSecurity.RestrictFile(logPath);
            }
            catch (IOException) { }
            MessageBox.Show(args.Exception.Message, "marsianskiy — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instanceMutex = new Mutex(true, @"Local\marsianskiy", out var created);
        _ownsMutex = created;
        if (!created)
        {
            MessageBox.Show("marsianskiy уже запущен. Используйте окно или значок в трее.", "marsianskiy", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        var verify = e.Args.FirstOrDefault(x => x.StartsWith("--verify=", StringComparison.Ordinal));
        var live = e.Args.FirstOrDefault(x => x.StartsWith("--live=", StringComparison.Ordinal));
        if (verify is null && live is null)
        {
            new MainWindow().Show();
            return;
        }
        var reportPath = live is null ? verify![9..] : live[7..] + ".report";
        try
        {
            var report = live is null ? await Diagnostics.VerifyAsync() : await Diagnostics.VerifyLiveAsync(live[7..]);
            File.WriteAllText(reportPath, report);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(reportPath, "FAILED\n" + SecretRedactor.Redact(ex.ToString()));
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
