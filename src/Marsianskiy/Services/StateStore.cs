using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Marsianskiy.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "marsianskiy");
    public static string RuntimeDirectory { get; } = Path.Combine(DataDirectory, "runtime");
    public static string ServicesFile { get; } = Path.Combine(DataDirectory, "services.json");
    private static string StateFile { get; } = Path.Combine(DataDirectory, "state.bin");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        FileSecurity.RestrictDirectory(DataDirectory);
        FileSecurity.RestrictDirectory(RuntimeDirectory);
    }

    public static string StatePath => StateFile;
}

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("marsianskiy.local.state.v1"));

    public AppState Load()
    {
        AppPaths.EnsureDirectories();
        CleanupStaleRuntimeFiles();
        if (!File.Exists(AppPaths.StatePath)) return new AppState();
        try
        {
            var encrypted = File.ReadAllBytes(AppPaths.StatePath);
            var json = Dpapi.Unprotect(encrypted, Entropy);
            return JsonSerializer.Deserialize<AppState>(json, JsonOptions) ?? new AppState();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            throw new InvalidDataException("Локальные данные не удалось расшифровать или прочитать. Файл состояния не был удалён; восстановите доступ текущего пользователя Windows или замените файл резервной копией.", ex);
        }
    }

    public void Save(AppState state)
    {
        AppPaths.EnsureDirectories();
        var json = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var encrypted = Dpapi.Protect(json, Entropy);
        var temporary = AppPaths.StatePath + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        FileSecurity.RestrictFile(temporary);
        File.Move(temporary, AppPaths.StatePath, true);
        FileSecurity.RestrictFile(AppPaths.StatePath);
    }

    public static void CleanupStaleRuntimeFiles()
    {
        if (!Directory.Exists(AppPaths.RuntimeDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(AppPaths.RuntimeDirectory, "mihomo-*.yaml"))
        {
            try { File.Delete(file); } catch (IOException) { }
        }
        foreach (var directory in Directory.EnumerateDirectories(AppPaths.RuntimeDirectory, "mihomo-*"))
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}

internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    public static byte[] Protect(byte[] value, byte[] entropy) => Transform(value, entropy, true);
    public static byte[] Unprotect(byte[] value, byte[] entropy) => Transform(value, entropy, false);

    private static byte[] Transform(byte[] value, byte[] entropy, bool protect)
    {
        var input = Allocate(value);
        var extra = Allocate(entropy);
        var output = new DataBlob();
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, "marsianskiy", ref extra, IntPtr.Zero, IntPtr.Zero, 0, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, 0, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero) Marshal.FreeHGlobal(input.Data);
            if (extra.Data != IntPtr.Zero) Marshal.FreeHGlobal(extra.Data);
            if (output.Data != IntPtr.Zero) Marshal.FreeHGlobal(output.Data);
        }
    }

    private static DataBlob Allocate(byte[] value)
    {
        var memory = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, memory, value.Length);
        return new DataBlob { Length = value.Length, Data = memory };
    }
}
