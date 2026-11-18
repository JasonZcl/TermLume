using System.Text.Json;
using System.Text.RegularExpressions;

namespace TermLume.Shared.Services;

public sealed class AegisOptions
{
    public string Issuer { get; set; } = "http://localhost:18080";
    public string ClientId { get; set; } = "termlume-desktop";
    public string RedirectUri { get; set; } = "http://127.0.0.1:47831/callback/";

    public static AegisOptions Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "aegis-iam.json");
        var options = File.Exists(path)
            ? JsonSerializer.Deserialize<AegisOptions>(File.ReadAllText(path), WorkspaceStore.JsonOptions)
                ?? throw new InvalidDataException("Aegis-IAM 配置无效。")
            : new AegisOptions();
        options.Issuer = Environment.GetEnvironmentVariable("TERMLUME_IAM_ISSUER") ?? options.Issuer;
        options.ClientId = Environment.GetEnvironmentVariable("TERMLUME_IAM_CLIENT_ID") ?? options.ClientId;
        options.RedirectUri = Environment.GetEnvironmentVariable("TERMLUME_IAM_REDIRECT_URI") ?? options.RedirectUri;
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer) || issuer.UserInfo.Length != 0 ||
            issuer.AbsolutePath != "/" || issuer.Query.Length != 0 || issuer.Fragment.Length != 0 ||
            (issuer.Scheme != "https" && !(issuer.Scheme == "http" && issuer.IsLoopback)) ||
            Issuer.Contains('\\') || Issuer.Any(char.IsControl))
            throw new InvalidDataException("Aegis-IAM 签发者必须为 HTTPS 来源，本机回环地址允许 HTTP。\n例如：http://localhost:18080");
        Issuer = Issuer.TrimEnd('/');
        if (string.IsNullOrEmpty(ClientId) || !Regex.IsMatch(ClientId, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{2,63}\z"))
            throw new InvalidDataException("Aegis-IAM 应用标识必须为 3–64 位字母、数字、点、下划线或连字符。");
        // Aegis-IAM 精确匹配回调 URL；固定 IPv4 回环端口也避免 localhost 的解析差异。
        if (!Uri.TryCreate(RedirectUri, UriKind.Absolute, out var redirect) || redirect.Scheme != "http" ||
            redirect.Host != "127.0.0.1" || redirect.IsDefaultPort || redirect.Port < 1024 ||
            redirect.UserInfo.Length != 0 || redirect.Query.Length != 0 || redirect.Fragment.Length != 0 ||
            redirect.AbsolutePath == "/" || RedirectUri.Contains('\\') || RedirectUri.Any(char.IsControl))
            throw new InvalidDataException("Aegis-IAM 回调必须为带固定端口和路径的 127.0.0.1 HTTP 地址。");
    }
}
