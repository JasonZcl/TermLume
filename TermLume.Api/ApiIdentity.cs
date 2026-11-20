using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TermLume.Shared.Services;

namespace TermLume.Api;

public sealed record ApiIdentity(string Issuer, string Subject, string GrantId = "", string DisplayName = "", Guid? DeviceId = null, string Version = "0.8.1");

public sealed class AegisAccessTokenValidator : IDisposable
{
    private readonly string issuer;
    private readonly string backchannel;
    private readonly string clientId;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 256 * 1024 };
    private readonly SemaphoreSlim keyGate = new(1, 1);
    private IReadOnlyCollection<SecurityKey> keys = [];
    private DateTimeOffset keysLoaded;
    private DateTimeOffset forcedReload;

    public AegisAccessTokenValidator(IConfiguration configuration)
    {
        var options = new AegisOptions { Issuer = configuration["Aegis:Issuer"] ?? "", ClientId = configuration["Aegis:ClientId"] ?? "" };
        options.Validate();
        issuer = options.Issuer;
        clientId = options.ClientId;
        backchannel = configuration["Aegis:BackchannelUrl"] ?? issuer;
        if (!Uri.TryCreate(backchannel, UriKind.Absolute, out var address) || address.UserInfo.Length != 0 || address.AbsolutePath != "/" ||
            address.Query.Length != 0 || address.Fragment.Length != 0 || backchannel.Contains('\\') || backchannel.Any(char.IsControl) ||
            address.Scheme != "https" && !(address.Scheme == "http" && (address.IsLoopback ||
                address.Host == "host.docker.internal" && configuration.GetValue<bool>("Aegis:AllowLocalDockerBackchannel"))))
            throw new InvalidDataException("IAM backchannel must use HTTPS; local Docker access requires explicit configuration.");
        backchannel = backchannel.TrimEnd('/');
    }

    public async Task<ApiIdentity?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 16384) return null;
        await LoadKeysAsync(false, cancellationToken);
        var result = await ValidateSignatureAsync(token);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            await LoadKeysAsync(true, cancellationToken);
            result = await ValidateSignatureAsync(token);
        }
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt || jwt.Typ != "at+jwt") return null;
        var user = new ClaimsPrincipal(result.ClaimsIdentity);
        var subject = user.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 255 || user.FindFirst("token_use")?.Value != "oauth_access" ||
            user.FindFirst("client_id")?.Value != clientId || jwt.Audiences.Count() != 1 ||
            user.FindFirst("sid")?.Value is not { Length: > 0 and <= 255 } || user.FindFirst("gid")?.Value is not { Length: > 0 and <= 255 } ||
            !(user.FindFirst("scope")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid") ?? false)) return null;
        // 公共桌面客户端不能使用机密客户端内省。UserInfo 对授权、原会话和安全戳执行实时校验。
        using var request = new HttpRequestMessage(HttpMethod.Get, backchannel + "/oauth/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("IAM validation unavailable.");
        using var profile = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return profile.RootElement.TryGetProperty("sub", out var actual) && actual.ValueKind == JsonValueKind.String && actual.GetString() == subject
            ? new ApiIdentity(issuer, subject, user.FindFirst("gid")!.Value,
                profile.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? new string(name.GetString()!.Where(c => !char.IsControl(c)).Take(100).ToArray()) : subject) : null;
    }

    private Task<TokenValidationResult> ValidateSignatureAsync(string token) => new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(token,
        new TokenValidationParameters
        {
            ValidIssuer = issuer, ValidAudience = clientId, IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = ["at+jwt"],
            RequireSignedTokens = true, RequireExpirationTime = true, ValidateIssuer = true, ValidateAudience = true,
            ValidateLifetime = true, ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.FromSeconds(30)
        });

    private async Task LoadKeysAsync(bool force, CancellationToken cancellationToken)
    {
        await keyGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (keys.Count > 0 && (force ? now - forcedReload < TimeSpan.FromSeconds(10) : now - keysLoaded < TimeSpan.FromMinutes(5))) return;
            if (force) forcedReload = now;
            using var response = await http.GetAsync(backchannel + "/.well-known/jwks.json", cancellationToken);
            response.EnsureSuccessStatusCode();
            var set = new JsonWebKeySet(await response.Content.ReadAsStringAsync(cancellationToken));
            var valid = set.Keys.Where(key => key.Kty == "RSA" && key.Use is null or "sig" && key.Alg is null or "RS256" && !string.IsNullOrEmpty(key.Kid) && key.D is null)
                .Where(key => Base64UrlEncoder.DecodeBytes(key.N).Length >= 256).Cast<SecurityKey>().ToArray();
            if (valid.Length == 0) throw new HttpRequestException("No trusted signing keys.");
            keys = valid;
            keysLoaded = DateTimeOffset.UtcNow;
        }
        finally { keyGate.Release(); }
    }

    public void Dispose() { http.Dispose(); keyGate.Dispose(); }
}
