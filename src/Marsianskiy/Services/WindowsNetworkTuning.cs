using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Marsianskiy.Services;

public static class WindowsNetworkTuning
{
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern bool DnsFlushResolverCache();

    public static async Task PrepareTunInterfaceAsync()
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("Set-NetIPInterface -InterfaceAlias 'marsianskiy' -InterfaceMetric 1 -ErrorAction Stop; foreach ($prefix in @('0.0.0.0/1','128.0.0.0/1')) { if (-not (Get-NetRoute -InterfaceAlias 'marsianskiy' -DestinationPrefix $prefix -ErrorAction SilentlyContinue)) { New-NetRoute -InterfaceAlias 'marsianskiy' -DestinationPrefix $prefix -NextHop '198.18.0.1' -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null } }");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось настроить приоритет TUN DNS.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            throw new TimeoutException("Настройка приоритета TUN DNS превысила 10 секунд.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Windows не смогла назначить приоритет DNS интерфейса marsianskiy: " + await process.StandardError.ReadToEndAsync());
        await WindowsDnsGuard.ApplyAsync();
        DnsFlushResolverCache();
    }
}
