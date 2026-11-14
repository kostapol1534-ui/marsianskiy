using System.Diagnostics;
using System.Text;

namespace Marsianskiy.Services;

public sealed class MihomoEngine
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EngineInstance? _instance;
    private bool _stopping;

    public event Action<string>? LogReceived;
    public event Action<string>? UnexpectedExit;
    public bool IsRunning => _instance?.IsRunning == true;
    public string EnginePath { get; } = Path.Combine(AppContext.BaseDirectory, "Engine", "mihomo.exe");

    public async Task StartAsync(string configuration, IReadOnlyList<VpnServer> servers)
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(EnginePath)) throw new FileNotFoundException("Не найден Engine/mihomo.exe. Переустановите приложение: движок должен находиться рядом с marsianskiy.exe.");
            await StopCoreAsync();
            _stopping = false;
            var instance = await StartCoreAsync(configuration, servers, true);
            instance.Process.Exited += (_, _) =>
            {
                if (!_stopping)
                {
                    var message = instance.ExitText;
                    instance.Cleanup();
                    UnexpectedExit?.Invoke(string.IsNullOrWhiteSpace(message) ? "Mihomo неожиданно завершил работу." : "Mihomo неожиданно завершил работу: " + message);
                }
            };
            _instance = instance;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        if (_instance is null) return;
        _stopping = true;
        await _instance.DisposeAsync();
        _instance = null;
        _stopping = false;
    }

    public async Task ValidateAsync(string configuration, IReadOnlyList<VpnServer> servers)
    {
        AppPaths.EnsureDirectories();
        var token = Guid.NewGuid().ToString("N");
        var configPath = Path.Combine(AppPaths.RuntimeDirectory, $"mihomo-{token}.yaml");
        var workDirectory = Path.Combine(AppPaths.RuntimeDirectory, $"mihomo-{token}");
        Directory.CreateDirectory(workDirectory);
        FileSecurity.RestrictDirectory(workDirectory);
        await File.WriteAllTextAsync(configPath, configuration, new UTF8Encoding(false));
        FileSecurity.RestrictFile(configPath);
        try
        {
            var check = await RunCheckAsync(configPath, workDirectory, servers);
            if (check.ExitCode != 0) throw new InvalidDataException("Mihomo отклонил конфигурацию: " + SecretRedactor.Redact(check.Output, servers));
        }
        finally { TryDelete(configPath, workDirectory); }
    }

    internal async Task<EngineInstance> StartEphemeralAsync(string configuration, IReadOnlyList<VpnServer> servers) =>
        await StartCoreAsync(configuration, servers, false);

    private async Task<EngineInstance> StartCoreAsync(string configuration, IReadOnlyList<VpnServer> servers, bool main)
    {
        AppPaths.EnsureDirectories();
        var token = Guid.NewGuid().ToString("N");
        var configPath = Path.Combine(AppPaths.RuntimeDirectory, $"mihomo-{token}.yaml");
        var workDirectory = Path.Combine(AppPaths.RuntimeDirectory, $"mihomo-{token}");
        Directory.CreateDirectory(workDirectory);
        FileSecurity.RestrictDirectory(workDirectory);
        await File.WriteAllTextAsync(configPath, configuration, new UTF8Encoding(false));
        FileSecurity.RestrictFile(configPath);
        try
        {
            var check = await RunCheckAsync(configPath, workDirectory, servers);
            if (check.ExitCode != 0)
                throw new InvalidDataException("Mihomo отклонил конфигурацию: " + SecretRedactor.Redact(check.Output, servers));
            var process = StartProcess(configPath, workDirectory, out var output, out var error);
            var instance = new EngineInstance(process, configPath, workDirectory, output, error);
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) LogReceived?.Invoke(SecretRedactor.Redact(args.Data, servers)); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) LogReceived?.Invoke(SecretRedactor.Redact(args.Data, servers)); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await Task.Delay(main ? 1000 : 350);
            if (process.HasExited)
            {
                var details = instance.ExitText;
                instance.Cleanup();
                throw new InvalidOperationException("Mihomo завершил запуск с ошибкой: " + SecretRedactor.Redact(details, servers));
            }
            return instance;
        }
        catch
        {
            TryDelete(configPath, workDirectory);
            throw;
        }
    }

    private Process StartProcess(string configPath, string workDirectory, out StringBuilder output, out StringBuilder error)
    {
        output = new StringBuilder();
        error = new StringBuilder();
        var start = new ProcessStartInfo(EnginePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDirectory
        };
        start.ArgumentList.Add("-d");
        start.ArgumentList.Add(workDirectory);
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add(configPath);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var capturedOutput = output;
        var capturedError = error;
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) lock (capturedOutput) capturedOutput.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) lock (capturedError) capturedError.AppendLine(args.Data); };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить mihomo.");
        return process;
    }

    private async Task<(int ExitCode, string Output)> RunCheckAsync(string configPath, string workDirectory, IReadOnlyList<VpnServer> servers)
    {
        var start = new ProcessStartInfo(EnginePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDirectory
        };
        start.ArgumentList.Add("-d");
        start.ArgumentList.Add(workDirectory);
        start.ArgumentList.Add("-t");
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add(configPath);
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) lock (output) output.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) lock (output) output.AppendLine(args.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw new TimeoutException("Проверка конфигурации mihomo превысила 20 секунд.");
        }
        process.WaitForExit();
        return (process.ExitCode, SecretRedactor.Redact(output.ToString(), servers));
    }

    private static void TryDelete(string configPath, string workDirectory)
    {
        try { if (File.Exists(configPath)) File.Delete(configPath); } catch (IOException) { }
        try { if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, true); } catch (IOException) { }
    }

    internal sealed class EngineInstance(Process process, string configPath, string workDirectory, StringBuilder output, StringBuilder error) : IAsyncDisposable
    {
        private int _disposed;
        public Process Process { get; } = process;
        public bool IsRunning => Interlocked.CompareExchange(ref _disposed, 0, 0) == 0 && !Process.HasExited;
        public string ExitText { get { lock (error) lock (output) return (error + Environment.NewLine + output).Trim(); } }

        public ValueTask DisposeAsync() => DisposeAsyncCore();

        private async ValueTask DisposeAsyncCore()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                if (!Process.HasExited) Process.Kill(true);
                await Process.WaitForExitAsync();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            Cleanup();
            Process.Dispose();
        }

        public void Cleanup()
        {
            TryDelete(configPath, workDirectory);
        }
    }
}
