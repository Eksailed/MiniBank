using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using MiniBank.Application.Abstractions;

namespace MiniBank.Api.Auth;

/// <summary>Отзыв сессий: токен, выданный раньше последней смены пароля, отклоняется.</summary>
internal static class SessionRevocation
{
    public static async Task CheckAsync(TokenValidatedContext context)
    {
        if (context.SecurityToken is not JsonWebToken token)
        {
            context.Fail("Unsupported token type.");
            return;
        }

        var users = context.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
        var user = await users.FindByIdAsync(context.Principal!.GetUserId(), context.HttpContext.RequestAborted);

        // Отсутствующего пользователя здесь не трогаем: его обрабатывают сами эндпоинты, как и раньше.
        if (user is not null && user.IsSessionRevoked(new DateTimeOffset(token.IssuedAt, TimeSpan.Zero)))
            context.Fail("The token was issued before the last password change.");
    }
}
