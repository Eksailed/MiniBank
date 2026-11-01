using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MiniBank.Application.Abstractions;
using MiniBank.Application.Auth;
using MiniBank.Domain;

namespace MiniBank.Api.Auth;

internal sealed class JwtTokenIssuer(JwtOptions options) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _credentials =
        new(new SymmetricSecurityKey(options.GetKeyBytes()), SecurityAlgorithms.HmacSha256);

    public AccessToken Issue(User user, DateTimeOffset now)
    {
        var expiresAt = now.AddMinutes(options.LifetimeMinutes);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString())]),
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = _credentials
        });
        return new AccessToken(token, expiresAt);
    }
}
