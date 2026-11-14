namespace Marsianskiy.Services;

public static class DesktopShortcutService
{
    public static string Create()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "marsianskiy.exe");
        if (!File.Exists(target)) throw new FileNotFoundException("Не найден marsianskiy.exe для создания ярлыка.");
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Компонент Windows Script Host недоступен.");
        var shell = Activator.CreateInstance(shellType) ?? throw new InvalidOperationException("Не удалось создать системный ярлык.");
        dynamic shellObject = shell;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "marsianskiy.lnk");
        dynamic shortcut = shellObject.CreateShortcut(path);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = AppContext.BaseDirectory;
        shortcut.IconLocation = target + ",0";
        shortcut.Description = "marsianskiy — split-routing VPN";
        shortcut.Save();
        return path;
    }
}
