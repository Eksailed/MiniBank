using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace MiniBank.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    /// <summary>Id пользователя из claim <c>sub</c> (в JwtBearer отключено переименование входящих claim-ов).</summary>
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
            ? id
            : throw new InvalidOperationException("The authenticated principal has no valid 'sub' claim.");
}
