using System.Text;
using System.Text.Json;

namespace Marsianskiy.Services;

public static class MihomoConfigBuilder
{
    public static string EngineVersion { get; set; } = "1.19.33";

    public static string BuildMain(IReadOnlyList<VpnServer> servers, IReadOnlyList<ServiceDefinition> services, bool includeGoogleSignIn)
    {
        if (servers.Count == 0) throw new InvalidOperationException("Сначала импортируйте хотя бы один сервер.");
        var enabled = services.Where(x => x.Enabled).ToList();
        if (enabled.Count == 0) throw new InvalidOperationException("Включите маршрутизацию хотя бы для одного сервиса.");
        var selected = enabled.ToDictionary(x => x.Id, x => TargetResolver.Resolve(x.Target, servers));

        var yaml = new YamlBuilder();
        yaml.Line("log-level: warning")
            .Line("mode: rule")
            .Line("ipv6: true")
            .Line("allow-lan: false")
            .Line("unified-delay: true")
            .Line("tcp-concurrent: true")
            .Line("find-process-mode: off")
            .Line("global-client-fingerprint: chrome")
            .Line("profile:").Line("  store-selected: false")
            .Line("tun:")
            .Line("  enable: true")
            .Line("  stack: gvisor")
            .Line("  device: marsianskiy")
            .Line("  auto-route: true")
            .Line("  route-address:")
            .Line("    - 0.0.0.0/1")
            .Line("    - 128.0.0.0/1")
            .Line("    - ::/1")
            .Line("    - 8000::/1")
            .Line("  auto-detect-interface: true")
            .Line("  strict-route: true")
            .Line("  dns-mode: disabled")
            .Line("  mtu: 1500")
            .Line("dns:")
            .Line("  enable: true")
            .Line("  listen: 127.0.0.1:53")
            .Line("  ipv6: true")
            .Line("  enhanced-mode: redir-host")
            .Line("  respect-rules: false")
            .Line("  use-system-hosts: true")
            .Line("  nameserver:")
            .Line("    - 77.88.8.8")
            .Line("    - 77.88.8.1")
            .Line("  direct-nameserver:")
            .Line("    - 77.88.8.8")
            .Line("    - 77.88.8.1")
            .Line("  direct-nameserver-follow-policy: false")
            .Line("  proxy-server-nameserver:")
            .Line("    - 77.88.8.8")
            .Line("    - 77.88.8.1")
            .Line("  nameserver-policy:");
        foreach (var service in enabled)
        {
            var node = selected[service.Id].NodeTag;
            foreach (var domain in service.ActiveDomains(includeGoogleSignIn).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                yaml.Line($"    {Quote("+." + domain)}:");
                yaml.Line($"      - {Quote("tcp://77.88.8.8#" + node)}");
                yaml.Line($"      - {Quote("tcp://77.88.8.1#" + node)}");
            }
        }

        WriteProxies(yaml, servers);
        yaml.Line("rules:");
        foreach (var service in enabled)
        {
            var node = selected[service.Id].NodeTag;
            foreach (var domain in service.ActiveDomains(includeGoogleSignIn).Distinct(StringComparer.OrdinalIgnoreCase))
                yaml.Line($"  - {Quote($"DOMAIN-SUFFIX,{domain},{node}")}");
            foreach (var processName in service.ProcessNames)
                yaml.Line($"  - {Quote($"PROCESS-NAME,{processName},{node}")}");
            foreach (var cidr in service.IpCidrs)
            {
                var type = cidr.Contains(':') ? "IP-CIDR6" : "IP-CIDR";
                yaml.Line($"  - {Quote($"{type},{cidr},{node},no-resolve")}");
            }
        }
        yaml.Line("  - MATCH,DIRECT");
        return yaml.ToString();
    }

    public static string BuildMeasurement(VpnServer server, int mixedPort)
    {
        var yaml = new YamlBuilder();
        yaml.Line("log-level: warning")
            .Line("mode: rule")
            .Line("ipv6: true")
            .Line("allow-lan: false")
            .Line($"mixed-port: {mixedPort}")
            .Line("global-client-fingerprint: chrome")
            .Line("dns:")
            .Line("  enable: true")
            .Line("  enhanced-mode: redir-host")
            .Line("  nameserver:")
            .Line("    - 77.88.8.8")
            .Line("    - 77.88.8.1")
            .Line("  proxy-server-nameserver:")
            .Line("    - 77.88.8.8")
            .Line("    - 77.88.8.1");
        WriteProxies(yaml, [server]);
        yaml.Line("rules:").Line($"  - {Quote("MATCH," + server.NodeTag)}");
        return yaml.ToString();
    }

    private static void WriteProxies(YamlBuilder yaml, IEnumerable<VpnServer> servers)
    {
        yaml.Line("proxies:");
        foreach (var server in servers)
        {
            yaml.Line($"  - name: {Quote(server.NodeTag)}")
                .Line($"    type: {(server.Protocol == ServerProtocol.VlessReality ? "vless" : "hysteria2")}")
                .Line($"    server: {Quote(server.Host)}")
                .Line($"    port: {server.Port}")
                .Line("    udp: true");
            if (server.Protocol == ServerProtocol.VlessReality)
            {
                yaml.Line($"    uuid: {Quote(server.Uuid)}")
                    .Line($"    tls: true")
                    .Line($"    servername: {Quote(server.ServerName)}")
                    .Line("    network: tcp")
                    .Line("    encryption: none")
                    .Line($"    client-fingerprint: {Quote(server.ClientFingerprint)}")
                    .Line("    reality-opts:")
                    .Line($"      public-key: {Quote(server.RealityPublicKey)}");
                if (server.RealityShortId.Length > 0) yaml.Line($"      short-id: {Quote(server.RealityShortId)}");
                if (server.Flow.Length > 0) yaml.Line($"    flow: {Quote(server.Flow)}");
            }
            else
            {
                yaml.Line($"    password: {Quote(server.Password)}")
                    .Line($"    sni: {Quote(server.ServerName)}")
                    .Line($"    skip-cert-verify: {(server.SkipCertVerify ? "true" : "false")}");
                if (server.Ports.Length > 0) yaml.Line($"    ports: {Quote(server.Ports)}");
                if (server.HopInterval > 0) yaml.Line($"    hop-interval: {server.HopInterval}");
                if (server.UpMbps > 0) yaml.Line($"    up: {Quote(server.UpMbps + " Mbps")}");
                if (server.DownMbps > 0) yaml.Line($"    down: {Quote(server.DownMbps + " Mbps")}");
                if (server.Obfs.Length > 0)
                    yaml.Line($"    obfs: {Quote(server.Obfs)}").Line($"    obfs-password: {Quote(server.ObfsPassword)}");
            }
        }
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private sealed class YamlBuilder
    {
        private readonly StringBuilder _text = new();
        public YamlBuilder Line(string line) { _text.Append(line).Append('\n'); return this; }
        public override string ToString() => _text.ToString();
    }
}

public static class TargetResolver
{
    public static VpnServer Resolve(string target, IReadOnlyList<VpnServer> servers)
    {
        if (target.StartsWith("server:", StringComparison.Ordinal))
        {
            var id = target[7..];
            return servers.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Выбранный сервер больше отсутствует в подписке. Выберите другой.");
        }
        if (target.StartsWith("country:", StringComparison.Ordinal))
        {
            var country = target[8..];
            var candidates = servers.Where(x => x.Country.Equals(country, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0) throw new InvalidOperationException($"Серверы страны «{country}» больше отсутствуют в подписке.");
            return Fastest(candidates) ?? throw new InvalidOperationException($"Для страны «{country}» ещё нет доступного измеренного сервера. Нажмите «Автовыбор» или выберите конкретный сервер.");
        }
        return Fastest(servers) ?? throw new InvalidOperationException("Нет доступного сервера с измеренной задержкой. Нажмите «Автовыбор» или выберите конкретный сервер.");
    }

    private static VpnServer? Fastest(IEnumerable<VpnServer> servers) => servers
        .Where(x => x.LatencyMs is > 0)
        .OrderBy(x => x.LatencyMs)
        .ThenBy(x => x.Name, StringComparer.CurrentCulture)
        .FirstOrDefault();
}
