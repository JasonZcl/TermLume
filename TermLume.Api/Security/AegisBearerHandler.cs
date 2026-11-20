using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Volo.Abp.Security.Claims;

namespace TermLume.Api.Security;

/// <summary>将签名及实时授权均通过 Aegis 校验的身份交给 ASP.NET Core 和 ABP 授权管线。</summary>
public sealed class AegisBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AegisAccessTokenValidator validator) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AegisBearer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Path.StartsWithSegments("/v1")) return AuthenticateResult.NoResult();
        var authorization = Request.Headers.Authorization;
        if (authorization.Count == 0) return AuthenticateResult.NoResult();
        if (authorization.Count != 1 || !authorization[0]!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("Invalid authorization header.");
        var identity = await validator.ValidateAsync(authorization[0]![7..], Context.RequestAborted);
        if (identity is null) return AuthenticateResult.Fail("Invalid or revoked access token.");

        Context.Features.Set(identity);
        var claims = new[]
        {
            new Claim(AbpClaimTypes.UserId, identity.Subject),
            new Claim(AbpClaimTypes.UserName, identity.DisplayName),
            new Claim("sub", identity.Subject),
            new Claim("iss", identity.Issuer),
            new Claim("gid", identity.GrantId)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
