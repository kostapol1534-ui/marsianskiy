using System.Text;

namespace Marsianskiy.Services;

public static class SupportReportService
{
    public static void Write(string path, MainViewModelState state)
    {
        var text = new StringBuilder()
            .AppendLine("marsianskiy support report")
            .AppendLine($"UTC: {DateTime.UtcNow:O}")
            .AppendLine($"Windows: {Environment.OSVersion.VersionString}")
            .AppendLine($"Mihomo: {MihomoConfigBuilder.EngineVersion}")
            .AppendLine($"VPN state: {state.VpnState}")
            .AppendLine($"Conflict: {state.Conflict}")
            .AppendLine("Enabled services: " + (state.EnabledServices.Count == 0 ? "none" : string.Join(", ", state.EnabledServices)))
            .AppendLine("Servers are omitted: no subscription URL, UUID, hostnames, REALITY keys or public IP addresses.")
            .AppendLine()
            .AppendLine("Recent sanitized engine messages:")
            .AppendLine(state.EngineMessages.Count == 0 ? "none" : string.Join(Environment.NewLine, state.EngineMessages));
        File.WriteAllText(path, text.ToString());
        FileSecurity.RestrictFile(path);
    }
}

public sealed record MainViewModelState(string VpnState, string Conflict, List<string> EnabledServices, List<string> EngineMessages);
