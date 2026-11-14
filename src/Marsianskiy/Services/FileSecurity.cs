using System.Security.AccessControl;
using System.Security.Principal;

namespace Marsianskiy.Services;

public static class FileSecurity
{
    public static void RestrictFile(string path)
    {
        var security = new System.Security.AccessControl.FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    public static void RestrictDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static string CurrentUser() => WindowsIdentity.GetCurrent().Name;
}

public static class SecretRedactor
{
    public static string Redact(string? text, IEnumerable<VpnServer>? servers = null, string subscriptionUrl = "")
    {
        if (string.IsNullOrEmpty(text)) return "";
        var result = text;
        var secrets = new List<string>();
        if (!string.IsNullOrWhiteSpace(subscriptionUrl)) secrets.Add(subscriptionUrl);
        if (servers is not null)
        {
            foreach (var server in servers)
                secrets.AddRange([server.Uuid, server.Password, server.RealityPublicKey, server.ObfsPassword]);
        }
        foreach (var secret in secrets.Where(x => x.Length >= 6).OrderByDescending(x => x.Length))
            result = result.Replace(secret, "••••••", StringComparison.Ordinal);
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\b(?:https?|vless|hysteria2|hy2)://[^\s\""']+", "<скрытый URL>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}\b", "••••••");
        return result;
    }
}
