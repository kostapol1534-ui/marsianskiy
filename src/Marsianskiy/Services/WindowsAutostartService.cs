using System.Diagnostics;

namespace Marsianskiy.Services;

public static class WindowsAutostartService
{
    private const string TaskName = "marsianskiy";

    public static async Task SetAsync(bool enabled)
    {
        var target = Path.Combine(AppContext.BaseDirectory, "marsianskiy.exe");
        if (enabled && !File.Exists(target)) throw new FileNotFoundException("Не найден marsianskiy.exe для автозапуска.");
        if (enabled)
            await RunAsync(["/Create", "/TN", TaskName, "/SC", "ONLOGON", "/RL", "HIGHEST", "/TR", $"\"{target}\"", "/F"]);
        else
            await RunAsync(["/Delete", "/TN", TaskName, "/F"]);
    }

    private static async Task RunAsync(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Планировщик заданий Windows.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw new TimeoutException("Настройка автозапуска превысила 15 секунд.");
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException("Планировщик заданий Windows: " + SecretRedactor.Redact(output + error));
    }
}
