using System.Security.Claims;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Results;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TermLume.Shared.Services;

internal sealed class AegisIdentityTokenValidator : IIdentityTokenValidator
{
    public string? ExpectedNonce { get; set; }
    public string? ExpectedSubject { get; set; }

    public async Task<IdentityTokenValidationResult> ValidateAsync(string identityToken, OidcClientOptions options, CancellationToken cancellationToken = default)
    {
        var keys = options.ProviderInformation.KeySet.Keys
            .Where(key => key.Kty == "RSA" && (string.IsNullOrEmpty(key.Use) || key.Use == "sig") &&
                (string.IsNullOrEmpty(key.Alg) || key.Alg == "RS256"))
            .Select(key => new JsonWebKey { Kty = key.Kty, Kid = key.Kid, N = key.N, E = key.E, Alg = "RS256", Use = "sig" });
        var result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(identityToken, new TokenValidationParameters
        {
            ValidIssuer = options.Authority, ValidAudience = options.ClientId,
            IssuerSigningKeys = keys, ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
            RequireSignedTokens = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name"
        });
        if (!result.IsValid) return new() { Error = "invalid_signature" };
        var user = new ClaimsPrincipal(result.ClaimsIdentity);
        if (string.IsNullOrEmpty(user.FindFirst("sub")?.Value) ||
            ExpectedNonce is not null && user.FindFirst("nonce")?.Value != ExpectedNonce ||
            ExpectedSubject is not null && user.FindFirst("sub")?.Value != ExpectedSubject ||
            user.FindFirst("azp") is { } azp && azp.Value != options.ClientId)
            return new() { Error = "invalid_identity_claims" };
        return new() { User = user, SignatureAlgorithm = ((JsonWebToken)result.SecurityToken).Alg };
    }
}
