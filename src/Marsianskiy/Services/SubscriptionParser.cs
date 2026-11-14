using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Marsianskiy.Services;

public static class SubscriptionParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ImportResult Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new FormatException("Подписка пуста. Вставьте URI серверов или base64-кодированный список URI.");
        if (content.Length > 4_000_000)
            throw new FormatException("Файл подписки больше 4 МБ и не будет импортирован.");

        var decoded = DecodeSubscription(content.Trim());
        var lines = SplitLines(decoded);
        if (lines.Count == 0)
            throw new FormatException("Формат не распознан: ожидается список vless://, hysteria2:// или hy2:// URI.");

        var servers = new List<VpnServer>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            try
            {
                var server = ParseUri(line);
                var fingerprint = string.Join('|', server.Protocol, server.Host, server.Port, server.Uuid, server.Password, server.RealityPublicKey);
                if (seen.Add(fingerprint)) servers.Add(server);
            }
            catch (Exception ex) when (ex is FormatException or UriFormatException)
            {
                if (errors.Count < 12) errors.Add(ex.Message);
            }
        }

        if (servers.Count == 0)
        {
            var detail = errors.Count > 0 ? " " + errors[0] : "";
            throw new FormatException("Не найдено ни одного поддерживаемого сервера." + detail);
        }
        return new ImportResult(servers, errors);
    }

    private static string DecodeSubscription(string content)
    {
        if (content.Contains("://", StringComparison.Ordinal)) return content;
        var compact = Regex.Replace(content, @"\s+", "");
        if (compact.Length == 0 || compact.Length % 4 == 1 || !Regex.IsMatch(compact, @"^[A-Za-z0-9_+\-/=]+$"))
            throw new FormatException("Формат не распознан: это ни список URI, ни base64-кодированная подписка.");
        try
        {
            var base64 = compact.Replace('-', '+').Replace('_', '/').TrimEnd('=');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var decoded = StrictUtf8.GetString(Convert.FromBase64String(base64));
            if (!decoded.Contains("://", StringComparison.Ordinal))
                throw new FormatException("Base64 декодирован, но внутри нет URI серверов.");
            return decoded;
        }
        catch (DecoderFallbackException)
        {
            throw new FormatException("Base64 декодирован, но содержимое не является UTF-8 списком URI.");
        }
        catch (FormatException ex) when (ex.Message.StartsWith("Base64", StringComparison.Ordinal))
        {
            throw;
        }
        catch (FormatException)
        {
            throw new FormatException("Не удалось декодировать base64-подписку. Проверьте содержимое.");
        }
    }

    private static List<string> SplitLines(string content)
    {
        var normalized = content.Replace("\r", "").Replace("\0", "");
        var raw = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>();
        foreach (var line in raw)
        {
            if (line.StartsWith('#')) continue;
            var parts = Regex.Split(line, @"(?=(?:vless|hysteria2|hy2)://)", RegexOptions.IgnoreCase)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0);
            result.AddRange(parts);
        }
        return result;
    }

    private static VpnServer ParseUri(string line)
    {
        if (!Uri.TryCreate(line, UriKind.Absolute, out var uri))
            throw new FormatException("Некорректный URI сервера.");
        if (uri.Scheme.Equals("vless", StringComparison.OrdinalIgnoreCase)) return ParseVless(uri);
        if (uri.Scheme.Equals("hysteria2", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("hy2", StringComparison.OrdinalIgnoreCase))
            return ParseHysteria2(uri);
        throw new FormatException($"Протокол «{uri.Scheme}» не поддерживается. Поддерживаются VLESS+REALITY и Hysteria2.");
    }

    private static VpnServer ParseVless(Uri uri)
    {
        var query = ParseQuery(uri.Query);
        if (!Guid.TryParse(DecodeUserInfo(uri.UserInfo), out _))
            throw new FormatException("VLESS: некорректный UUID.");
        if (uri.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(uri.Host))
            throw new FormatException("VLESS: не указан корректный адрес или порт.");
        if (!query.GetValueOrDefault("security", "").Equals("reality", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"VLESS «{SafeName(uri)}»: поддерживается только transport TCP с REALITY.");
        var network = query.GetValueOrDefault("type", "tcp");
        if (!network.Equals("tcp", StringComparison.OrdinalIgnoreCase) && network.Length != 0)
            throw new FormatException($"VLESS «{SafeName(uri)}»: транспорт {network} не поддерживается, нужен TCP.");
        var encryption = query.GetValueOrDefault("encryption", "none");
        if (!encryption.Equals("none", StringComparison.OrdinalIgnoreCase) && encryption.Length != 0)
            throw new FormatException($"VLESS «{SafeName(uri)}»: поддерживается encryption=none.");
        var publicKey = query.GetValueOrDefault("pbk", "");
        if (publicKey.Length == 0) throw new FormatException($"VLESS «{SafeName(uri)}»: отсутствует публичный ключ REALITY (pbk).");
        var flow = query.GetValueOrDefault("flow", "");
        if (flow.Length > 0 && flow != "xtls-rprx-vision")
            throw new FormatException($"VLESS «{SafeName(uri)}»: flow {flow} не поддерживается.");
        var name = SafeName(uri);
        return new VpnServer
        {
            Protocol = ServerProtocol.VlessReality,
            Host = uri.Host.Trim('[', ']'),
            Port = uri.Port,
            Name = name,
            Country = InferCountry(name),
            Uuid = DecodeUserInfo(uri.UserInfo),
            Flow = flow,
            RealityPublicKey = publicKey,
            RealityShortId = query.GetValueOrDefault("sid", ""),
            ServerName = query.GetValueOrDefault("sni", uri.Host),
            ClientFingerprint = NormalizeFingerprint(query.GetValueOrDefault("fp", "chrome"))
        };
    }

    private static VpnServer ParseHysteria2(Uri uri)
    {
        var query = ParseQuery(uri.Query);
        var password = DecodeUserInfo(uri.UserInfo);
        if (password.Length == 0) throw new FormatException("Hysteria2: отсутствует пароль.");
        if (uri.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(uri.Host))
            throw new FormatException("Hysteria2: не указан корректный адрес или порт.");
        var obfs = query.GetValueOrDefault("obfs", "");
        if (obfs.Length > 0 && obfs is not ("salamander" or "gecko"))
            throw new FormatException($"Hysteria2 «{SafeName(uri)}»: obfs {obfs} не поддерживается mihomo.");
        if (obfs.Length > 0 && query.GetValueOrDefault("obfs-password", "").Length == 0)
            throw new FormatException($"Hysteria2 «{SafeName(uri)}»: для obfs нужен obfs-password.");
        var name = SafeName(uri);
        return new VpnServer
        {
            Protocol = ServerProtocol.Hysteria2,
            Host = uri.Host.Trim('[', ']'),
            Port = uri.Port,
            Name = name,
            Country = InferCountry(name),
            Password = password,
            ServerName = query.GetValueOrDefault("sni", uri.Host),
            Obfs = obfs,
            ObfsPassword = query.GetValueOrDefault("obfs-password", ""),
            SkipCertVerify = ParseBool(query.GetValueOrDefault("insecure", "0")),
            Ports = query.GetValueOrDefault("mport", ""),
            HopInterval = ParsePositiveInt(query.GetValueOrDefault("hop-interval", "0")),
            UpMbps = ParsePositiveInt(query.GetValueOrDefault("upmbps", query.GetValueOrDefault("up", "0"))),
            DownMbps = ParsePositiveInt(query.GetValueOrDefault("downmbps", query.GetValueOrDefault("down", "0")))
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            var key = Decode(index < 0 ? pair : pair[..index]);
            var value = index < 0 ? "" : Decode(pair[(index + 1)..]);
            result[key] = value;
        }
        return result;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    private static string DecodeUserInfo(string value) => Uri.UnescapeDataString(value);
    private static string SafeName(Uri uri) => string.IsNullOrWhiteSpace(uri.Fragment) ? $"{uri.Scheme} · {uri.Host}" : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
    private static bool ParseBool(string value) => value is "1" or "true" or "yes";
    private static int ParsePositiveInt(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 0;

    private static string NormalizeFingerprint(string value)
    {
        string[] supported = ["chrome", "firefox", "safari", "ios", "android", "edge", "360", "qq", "random", "randomized"];
        return supported.Contains(value) ? value : "chrome";
    }

    private static string InferCountry(string name)
    {
        var code = "";
        foreach (var rune in name.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x1F1E6 and <= 0x1F1FF) code += (char)('A' + value - 0x1F1E6);
        }
        if (code.Length != 2)
        {
            var match = Regex.Match(name, @"(?<![A-Za-z])([A-Za-z]{2})(?![A-Za-z])");
            if (match.Success) code = match.Groups[1].Value.ToUpperInvariant();
        }
        if (code.Length != 2) return "Не указана";
        try { return new RegionInfo(code).DisplayName; }
        catch (ArgumentException) { return "Не указана"; }
    }
}
