using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Marsianskiy.Services;

public static class ServiceCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true };

    public static List<ServiceDefinition> Load()
    {
        AppPaths.EnsureDirectories();
        if (!File.Exists(AppPaths.ServicesFile))
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "services.json"), AppPaths.ServicesFile);
            FileSecurity.RestrictFile(AppPaths.ServicesFile);
        }
        List<ServiceDefinition> services;
        try
        {
            services = JsonSerializer.Deserialize<List<ServiceDefinition>>(File.ReadAllText(AppPaths.ServicesFile), JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Ошибка в services.json: {ex.Message}", ex);
        }
        var defaultServices = JsonSerializer.Deserialize<List<ServiceDefinition>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "services.json")), JsonOptions) ?? [];
        var missing = defaultServices.Where(definition => services.All(existing => !existing.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
        {
            services.AddRange(missing);
            var header = "// Маршруты marsianskiy. Не назначайте один домен/IP разным сервисам.\n// processNames направляет только игровые процессы, а не весь браузер/AWS.\n";
            File.WriteAllText(AppPaths.ServicesFile, header + JsonSerializer.Serialize(services, new JsonSerializerOptions { WriteIndented = true }));
            FileSecurity.RestrictFile(AppPaths.ServicesFile);
        }
        if (services.Count == 0) throw new InvalidDataException("В services.json нет ни одного сервиса.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in services)
        {
            if (string.IsNullOrWhiteSpace(service.Id) || !ids.Add(service.Id)) throw new InvalidDataException("В services.json есть пустой или повторяющийся id сервиса.");
            if (service.Domains.Count == 0 && service.IpCidrs.Count == 0) throw new InvalidDataException($"Для {service.Name} не заданы домены или IP-диапазоны.");
            service.Domains = service.Domains.Select(NormalizeDomain).ToList();
            service.OptionalDomains = service.OptionalDomains.Select(NormalizeDomain).ToList();
            foreach (var cidr in service.IpCidrs) ValidateCidr(cidr);
        }
        var overlaps = FindOverlaps(services).ToList();
        if (overlaps.Count > 0)
            throw new InvalidDataException("Пересечения маршрутов в services.json: " + string.Join("; ", overlaps.Take(6).Select(x => $"{x.Domain}: {x.FirstService} / {x.SecondService}")) + ". Исправьте списки, чтобы один домен/IP не назначался разным серверам.");
        return services;
    }

    public static IEnumerable<RouteOverlap> FindOverlaps(IEnumerable<ServiceDefinition> services)
    {
        var entries = services.SelectMany(s => s.Domains.Concat(s.OptionalDomains).Select(d => (Service: s.Name, Value: d, Domain: true))
            .Concat(s.IpCidrs.Select(ip => (Service: s.Name, Value: ip, Domain: false)))).ToList();
        for (var i = 0; i < entries.Count; i++)
        for (var j = i + 1; j < entries.Count; j++)
        {
            var a = entries[i];
            var b = entries[j];
            if (a.Service == b.Service || a.Domain != b.Domain) continue;
            var overlaps = a.Domain ? DomainOverlap(a.Value, b.Value) : CidrOverlap(a.Value, b.Value);
            if (overlaps) yield return new RouteOverlap(a.Domain ? $"{a.Value} ↔ {b.Value}" : $"{a.Value} / {b.Value}", a.Service, b.Service);
        }
    }

    private static string NormalizeDomain(string value)
    {
        var domain = value.Trim().TrimEnd('.').TrimStart('+', '*').TrimStart('.').ToLowerInvariant();
        if (domain.Length > 253 || !System.Text.RegularExpressions.Regex.IsMatch(domain, @"^(?=.{1,253}$)[a-z0-9_](?:[a-z0-9_.-]*[a-z0-9_])?$"))
            throw new InvalidDataException($"Некорректный домен в services.json: {value}");
        domain = new IdnMapping().GetAscii(domain);
        string[] forbidden = ["google.com", "googleapis.com", "gstatic.com", "cloudflare.com", "cloudflare.net"];
        if (forbidden.Contains(domain)) throw new InvalidDataException($"Домен {domain} слишком широкий и запрещён в маршрутах. Укажите конкретные поддомены нужного сервиса.");
        return domain;
    }

    private static bool DomainOverlap(string a, string b) => a == b || a.EndsWith('.' + b, StringComparison.Ordinal) || b.EndsWith('.' + a, StringComparison.Ordinal);

    private static void ValidateCidr(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix))
            throw new InvalidDataException($"Некорректный IP CIDR: {value}");
        var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix < 0 || prefix > bits) throw new InvalidDataException($"Некорректная маска IP CIDR: {value}");
    }

    private static bool CidrOverlap(string a, string b)
    {
        var left = ParseCidr(a);
        var right = ParseCidr(b);
        if (left.Address.AddressFamily != right.Address.AddressFamily) return false;
        var common = Math.Min(left.Prefix, right.Prefix);
        var x = left.Address.GetAddressBytes();
        var y = right.Address.GetAddressBytes();
        for (var bit = 0; bit < common; bit++)
        {
            var mask = 1 << (7 - bit % 8);
            if ((x[bit / 8] & mask) != (y[bit / 8] & mask)) return false;
        }
        return true;
    }

    private static (IPAddress Address, int Prefix) ParseCidr(string value)
    {
        var parts = value.Split('/');
        return (IPAddress.Parse(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }
}
