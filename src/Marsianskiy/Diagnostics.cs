using System.Text;
using Marsianskiy.Services;

namespace Marsianskiy;

public static class Diagnostics
{
    public static async Task<string> VerifyAsync()
    {
        const string vless = "vless://11111111-1111-4111-8111-111111111111@192.0.2.10:443?security=reality&encryption=none&flow=xtls-rprx-vision&pbk=MDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDA&sni=example.com&sid=0123&type=tcp#DE%20Diagnostics";
        const string hy2 = "hysteria2://local-password@192.0.2.20:443?sni=example.org&insecure=0#NL%20Diagnostics";
        var plain = SubscriptionParser.Parse(vless + "\n" + hy2);
        var encoded = SubscriptionParser.Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(vless + "\n" + hy2)));
        if (plain.Servers.Count != 2 || encoded.Servers.Count != 2) throw new InvalidDataException("Subscription parser verification failed.");
        var services = ServiceCatalog.Load();
        if (services.Count != 22) throw new InvalidDataException($"Expected 22 services, got {services.Count}.");
        if (ServiceCatalog.FindOverlaps(services).Any()) throw new InvalidDataException("Service route overlap detected.");
        services.First(x => x.Id == "twitch").Enabled = true;
        services.First(x => x.Id == "twitch").Target = "server:" + plain.Servers[0].Id;
        services.First(x => x.Id == "fortnite").Enabled = true;
        services.First(x => x.Id == "fortnite").Target = "server:" + plain.Servers[0].Id;
        services.First(x => x.Id == "telegram").Enabled = true;
        services.First(x => x.Id == "telegram").Target = "server:" + plain.Servers[1].Id;
        var config = MihomoConfigBuilder.BuildMain(plain.Servers, services, false);
        var engine = new MihomoEngine();
        await engine.ValidateAsync(config, plain.Servers);
        StateStore.CleanupStaleRuntimeFiles();
        return $"OK\nSubscription URI/base64: 2 nodes\nServices: {services.Count}, no overlaps\nMihomo {MihomoConfigBuilder.EngineVersion}: configuration accepted\n";
    }

    public static async Task<string> VerifyLiveAsync(string urlFile)
    {
        var url = (await File.ReadAllTextAsync(urlFile)).Trim();
        List<VpnServer> servers = [];
        var engine = new MihomoEngine();
        try
        {
            var content = await SubscriptionDownloader.DownloadAsync(url, CancellationToken.None);
            servers = SubscriptionParser.Parse(content).Servers;
            var report = new StringBuilder().AppendLine($"Subscription imported: {servers.Count} nodes");
            var tester = new LatencyTester(engine);
            var available = new List<VpnServer>();
            foreach (var server in servers)
            {
                try
                {
                    server.LatencyMs = await tester.MeasureAsync(server, CancellationToken.None);
                    available.Add(server);
                    report.AppendLine($"Available: {server.Name} / {server.Country} / {server.LatencyMs} ms");
                }
                catch (Exception ex)
                {
                    report.AppendLine($"Unavailable: {server.Name} / {SecretRedactor.Redact(ex.Message, servers, url)}");
                }
            }
            if (available.Count == 0) throw new InvalidOperationException("No subscription server passed the HTTPS check.");
            var fastest = available.OrderBy(x => x.LatencyMs).First();
            var directBefore = await GetDirectIpAsync();
            var services = ServiceCatalog.Load();
            var checkService = services.First(x => x.Id == "2ip");
            checkService.Enabled = true;
            checkService.Target = "server:" + fastest.Id;
            await engine.StartAsync(MihomoConfigBuilder.BuildMain(servers, services, false), servers);
            using var response = await CreateDirectClient().GetAsync("https://2ip.io/");
            report.AppendLine($"2ip through selected VPN: HTTP {(int)response.StatusCode}");
            var directAfter = await GetDirectIpAsync();
            if (directBefore.Length > 0 && directAfter.Length > 0 && directBefore != directAfter)
                throw new InvalidOperationException("Unselected traffic changed its public IP.");
            report.AppendLine($"Direct traffic unchanged: {directBefore.Length > 0 && directBefore == directAfter}");
            report.AppendLine($"Selected: {fastest.Name} / {fastest.LatencyMs} ms");
            return report.ToString();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(SecretRedactor.Redact(ex.Message, servers, url));
        }
        finally
        {
            await engine.StopAsync();
            StateStore.CleanupStaleRuntimeFiles();
        }
    }

    private static HttpClient CreateDirectClient()
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        client.Timeout = TimeSpan.FromSeconds(12);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("marsianskiy/1.0");
        return client;
    }

    private static async Task<string> GetDirectIpAsync()
    {
        try
        {
            using var client = CreateDirectClient();
            return (await client.GetStringAsync("https://api.ipify.org/")).Trim();
        }
        catch { return ""; }
    }
}
