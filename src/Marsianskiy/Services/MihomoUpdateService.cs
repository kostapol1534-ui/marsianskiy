using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Marsianskiy.Services;

public static class MihomoUpdateService
{
    public static async Task<string> UpdateAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("marsianskiy/1.0");
        client.Timeout = TimeSpan.FromSeconds(30);
        var release = await client.GetStringAsync("https://api.github.com/repos/MetaCubeX/mihomo/releases/latest", cancellationToken);
        using var document = JsonDocument.Parse(release);
        var tag = document.RootElement.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("В ответе GitHub нет tag_name.");
        var latest = tag.TrimStart('v');
        if (Version.Parse(latest) <= Version.Parse(MihomoConfigBuilder.EngineVersion)) return $"Уже установлена актуальная версия mihomo {MihomoConfigBuilder.EngineVersion}.";
        var assetName = $"mihomo-windows-amd64-compatible-v{latest}.zip";
        var asset = document.RootElement.GetProperty("assets").EnumerateArray().FirstOrDefault(x => x.GetProperty("name").GetString() == assetName);
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException($"В релизе {tag} нет {assetName}.");
        var url = asset.GetProperty("browser_download_url").GetString() ?? throw new InvalidDataException("В релизе нет ссылки на архив.");
        var expected = asset.GetProperty("digest").GetString()?.Replace("sha256:", "") ?? throw new InvalidDataException("В релизе нет SHA-256.");

        AppPaths.EnsureDirectories();
        var archive = Path.Combine(AppPaths.RuntimeDirectory, "mihomo-update.zip");
        var directory = Path.Combine(AppPaths.RuntimeDirectory, "mihomo-update");
        var backup = Path.Combine(AppPaths.RuntimeDirectory, "mihomo-backup.exe");
        try
        {
            await File.WriteAllBytesAsync(archive, await client.GetByteArrayAsync(url, cancellationToken), cancellationToken);
            FileSecurity.RestrictFile(archive);
            var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archive, cancellationToken))).ToLowerInvariant();
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 архива mihomo не совпал с официальным digest.");
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            ZipFile.ExtractToDirectory(archive, directory);
            var engineUpdate = Path.Combine(directory, "mihomo-windows-amd64-compatible.exe");
            if (!File.Exists(engineUpdate)) throw new FileNotFoundException("Бинарник mihomo не найден в архиве.");
            await ValidateBinaryAsync(engineUpdate, directory);
            var enginePath = Path.Combine(AppContext.BaseDirectory, "Engine", "mihomo.exe");
            File.Copy(enginePath, backup, true);
            FileSecurity.RestrictFile(backup);
            try
            {
                File.Copy(engineUpdate, enginePath, true);
                await ValidateBinaryAsync(enginePath, AppContext.BaseDirectory);
                MihomoConfigBuilder.EngineVersion = latest;
            }
            catch
            {
                File.Copy(backup, enginePath, true);
                throw;
            }
            return $"Mihomo обновлён до {latest}. Архив проверен по SHA-256, новая версия запущена для проверки.";
        }
        finally
        {
            try { if (File.Exists(archive)) File.Delete(archive); } catch (IOException) { }
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException) { }
            try { if (File.Exists(backup)) File.Delete(backup); } catch (IOException) { }
        }
    }

    private static async Task ValidateBinaryAsync(string path, string workingDirectory)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
        start.ArgumentList.Add("-v");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить mihomo для проверки.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Новый бинарник mihomo не прошёл проверку запуска.");
    }
}
