using System.Diagnostics;

namespace Marsianskiy.Services;

public static class NetworkConflictDetector
{
    public static async Task<IReadOnlyList<string>> GetActiveVpnAdaptersAsync()
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
        start.ArgumentList.Add("Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.Name -ne 'marsianskiy' -and ($_.Name -like 'Fixnet*' -or $_.InterfaceDescription -match 'Fixnet|OpenVPN|WireGuard|Wintun') } | ForEach-Object { $_.Name }");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось проверить конфликты VPN-адаптеров.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw new TimeoutException("Проверка конфликтных VPN-адаптеров превысила 10 секунд.");
        }
        var output = await outputTask;
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
