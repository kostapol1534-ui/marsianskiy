using System.Net;
using System.Text;

namespace Marsianskiy.Services;

public static class SubscriptionDownloader
{
    public static async Task<string> DownloadAsync(string address, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FormatException("Укажите локально известный вам URL подписки, начинающийся с https:// или http://.");
        if (uri.UserInfo.Length > 0) throw new FormatException("URL с логином и паролем в адресе не поддерживается. Используйте файл или буфер обмена.");

        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("marsianskiy/1.0");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Сервер подписки вернул HTTP {(int)response.StatusCode} ({response.StatusCode}). URL скрыт.");
        var length = response.Content.Headers.ContentLength;
        if (length > 4_000_000) throw new FormatException("Подписка больше 4 МБ и не будет импортирована.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > 4_000_000) throw new FormatException("Подписка больше 4 МБ и не будет импортирована.");
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }
}
