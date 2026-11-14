using System.Diagnostics;
using System.Text;

namespace Marsianskiy.Services;

public static class WindowsDnsGuard
{
    private static string BackupFile => Path.Combine(AppPaths.RuntimeDirectory, "dns-backup.txt");

    public static async Task ApplyAsync()
    {
        AppPaths.EnsureDirectories();
        await RestoreAsync();
        var capture = "Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -ne 'marsianskiy' -and $_.ServerAddresses.Count -gt 0 } | ForEach-Object { \"$($_.InterfaceIndex)|$($_.ServerAddresses -join ',')\" }";
        var backup = await RunPowerShellAsync(capture);
        await File.WriteAllTextAsync(BackupFile, backup, new UTF8Encoding(false));
        FileSecurity.RestrictFile(BackupFile);
        try
        {
            var script = new StringBuilder();
            script.Append("$backup = Get-Content -LiteralPath '").Append(Quote(BackupFile)).AppendLine("';");
            script.AppendLine("foreach ($line in $backup) { $parts = $line.Split('|'); if ($parts.Length -eq 2 -and $parts[0] -match '^\\d+$') { Set-DnsClientServerAddress -InterfaceIndex ([int]$parts[0]) -ServerAddresses '127.0.0.1' -ErrorAction Stop } }");
            await RunPowerShellAsync(script.ToString());
        }
        catch
        {
            await RestoreAsync();
            throw;
        }
    }

    public static async Task RestoreAsync()
    {
        if (!File.Exists(BackupFile)) return;
        var script = new StringBuilder();
        script.Append("$backup = Get-Content -LiteralPath '").Append(Quote(BackupFile)).AppendLine("';");
        script.AppendLine("$failed = $false; foreach ($line in $backup) { $parts = $line.Split('|'); if ($parts.Length -eq 2 -and $parts[0] -match '^\\d+$') { try { $addresses = $parts[1].Split(',', [System.StringSplitOptions]::RemoveEmptyEntries); Set-DnsClientServerAddress -InterfaceIndex ([int]$parts[0]) -ServerAddresses $addresses -ErrorAction Stop } catch { $failed = $true } } }; if ($failed) { throw 'DNS restore failed' }");
        await RunPowerShellAsync(script.ToString());
        File.Delete(BackupFile);
    }

    private static async Task<string> RunPowerShellAsync(string command)
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
        start.ArgumentList.Add(command);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить настройку DNS Windows.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw new TimeoutException("Настройка DNS Windows превысила 15 секунд.");
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows DNS guard: " + SecretRedactor.Redact(error));
        return output.Trim();
    }

    private static string Quote(string value) => value.Replace("'", "''");
}
