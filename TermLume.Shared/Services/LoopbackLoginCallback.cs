using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TermLume.Shared.Services;

// 不使用 HTTP.sys / URL ACL，不监听其他网卡；仅在交互登录期间开启。
internal sealed class LoopbackLoginCallback : IDisposable
{
    private readonly TcpListener listener;
    private readonly Uri redirect;

    public LoopbackLoginCallback(string redirectUri)
    {
        redirect = new Uri(redirectUri);
        listener = new TcpListener(IPAddress.Loopback, redirect.Port);
        listener.Server.ExclusiveAddressUse = true;
        try { listener.Start(8); }
        catch (SocketException) { listener.Stop(); throw new IOException("登录回调端口被占用，请关闭其他 TermLume 实例，或配置其他已登记端口。"); }
    }

    public async Task<string> ReceiveAsync(string state, string issuer, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(count), deadline.Token);
                    if (read == 0) break;
                    count += read;
                    if (Encoding.ASCII.GetString(buffer, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                var header = Encoding.ASCII.GetString(buffer, 0, count);
                var lines = header.Split("\r\n");
                var request = lines[0].Split(' ');
                var hosts = lines.Where(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
                var valid = header.EndsWith("\r\n\r\n", StringComparison.Ordinal) && request.Length == 3 &&
                    request[0] == "GET" && request[1].StartsWith('/') && !request[1].StartsWith("//") &&
                    request[2] == "HTTP/1.1" && hosts.Length == 1 && hosts[0][5..].Trim() == redirect.Authority &&
                    !lines.Any(line => line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase));
                Uri? callback = null;
                Dictionary<string, string>? parameters = null;
                if (valid)
                {
                    callback = new Uri(redirect.GetLeftPart(UriPartial.Authority) + request[1]);
                    valid = callback.AbsolutePath == redirect.AbsolutePath && callback.Fragment.Length == 0;
                    if (valid)
                    {
                        try { parameters = ParseQuery(callback.Query); }
                        catch (InvalidDataException) { valid = false; }
                    }
                    valid = valid && parameters!.GetValueOrDefault("state") == state &&
                        (parameters!.ContainsKey("error") || parameters.GetValueOrDefault("iss") == issuer) &&
                        (parameters.ContainsKey("code") ^ parameters.ContainsKey("error"));
                }
                var body = LoginCallbackPage.Render(valid, parameters?.ContainsKey("error") == true);
                var bytes = Encoding.UTF8.GetBytes(body);
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nContent-Security-Policy: {LoginCallbackPage.ContentSecurityPolicy}\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, deadline.Token);
                await stream.WriteAsync(bytes, deadline.Token);
                if (valid) return callback!.AbsoluteUri;
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or UriFormatException or InvalidDataException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            if (!result.TryAdd(key, value)) throw new InvalidDataException("Duplicate callback parameter.");
        }
        return result;
    }

    public void Dispose() => listener.Stop();
}
