using System.Net;
using System.Text.RegularExpressions;

namespace Marsianskiy.Services;

public static class ConnectionDiagnostics
{
    public static async Task<string> TestRouteAsync(CancellationToken cancellationToken)
    {
        var directIp = await GetTextAsync("https://api.ipify.org/", cancellationToken);
        var page = await GetTextAsync("https://2ip.io/", cancellationToken);
        var match = Regex.Match(page, @"(?<![\d])(?:\d{1,3}\.){3}\d{1,3}(?![\d])");
        if (!match.Success) throw new InvalidOperationException("2ip ответил, но публичный IP на странице не найден.");
        return match.Value == directIp.Trim()
            ? "Тест не пройден: 2ip и прямой трафик имеют одинаковый IP. Включите маршрутизацию 2ip.io и переподключите."
            : "Тест пройден: 2ip использует другой IP, а непомеченный трафик остаётся прямым. Публичные адреса в отчёт не записываются.";
    }

    private static async Task<string> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("marsianskiy/1.0");
        return await client.GetStringAsync(url, cancellationToken);
    }
}
