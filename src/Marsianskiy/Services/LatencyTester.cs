using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Marsianskiy.Services;

public sealed class LatencyTester(MihomoEngine engine)
{
    private const string TestHost = "www.gstatic.com";

    public async Task<int> MeasureAsync(VpnServer server, CancellationToken cancellationToken)
    {
        var port = GetFreePort();
        var config = MihomoConfigBuilder.BuildMeasurement(server, port);
        await using var instance = await engine.StartEphemeralAsync(config, [server]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var stopwatch = Stopwatch.StartNew();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await using var stream = client.GetStream();
        await SocksHandshakeAsync(stream, timeout.Token);
        await SocksConnectAsync(stream, TestHost, 443, timeout.Token);
        await using var tls = new SslStream(stream, false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = TestHost,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.None
        }, timeout.Token);
        var request = Encoding.ASCII.GetBytes($"GET /generate_204 HTTP/1.1\r\nHost: {TestHost}\r\nUser-Agent: marsianskiy/1.0\r\nConnection: close\r\n\r\n");
        await tls.WriteAsync(request, timeout.Token);
        var header = await ReadHeadersAsync(tls, timeout.Token);
        if (!header.StartsWith("HTTP/1.1 204", StringComparison.Ordinal) && !header.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) &&
            !header.StartsWith("HTTP/2 204", StringComparison.Ordinal) && !header.StartsWith("HTTP/2 200", StringComparison.Ordinal))
            throw new HttpRequestException("Тестовый HTTPS-запрос через сервер вернул неожиданный ответ.");
        stopwatch.Stop();
        return Math.Max(1, (int)stopwatch.ElapsedMilliseconds);
    }

    private static async Task SocksHandshakeAsync(NetworkStream stream, CancellationToken token)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
        var response = await ReadExactAsync(stream, 2, token);
        if (response[0] != 5 || response[1] != 0) throw new IOException("Локальный SOCKS5 mihomo не принял подключение.");
    }

    private static async Task SocksConnectAsync(NetworkStream stream, string host, int port, CancellationToken token)
    {
        var hostBytes = Encoding.ASCII.GetBytes(host);
        var request = new byte[7 + hostBytes.Length];
        request[0] = 5; request[1] = 1; request[2] = 0; request[3] = 3; request[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(request, 5);
        request[^2] = (byte)(port >> 8); request[^1] = (byte)port;
        await stream.WriteAsync(request, token);
        var head = await ReadExactAsync(stream, 4, token);
        if (head[0] != 5 || head[1] != 0) throw new IOException($"VPN-сервер не смог установить соединение (SOCKS код {head[1]}).");
        var remaining = head[3] switch { 1 => 4, 4 => 16, 3 => (await ReadExactAsync(stream, 1, token))[0], _ => throw new IOException("Некорректный SOCKS5 ответ mihomo.") };
        await ReadExactAsync(stream, remaining + 2, token);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken token)
    {
        var result = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(result.AsMemory(offset, length - offset), token);
            if (read == 0) throw new IOException("Соединение с VPN-сервером неожиданно закрыто.");
            offset += read;
        }
        return result;
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(2048);
        var buffer = new byte[1024];
        while (bytes.Count < 16384)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            bytes.AddRange(buffer.AsSpan(0, read).ToArray());
            if (bytes.Count >= 4 && bytes[^1] == 10 && bytes[^2] == 13 && bytes[^3] == 10 && bytes[^4] == 13) break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
