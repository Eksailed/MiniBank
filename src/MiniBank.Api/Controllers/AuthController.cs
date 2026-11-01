using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MiniBank.Application.Auth;

namespace MiniBank.Api.Controllers;

[ApiController]
[Route("auth")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public class AuthController(AuthService auth) : ControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        await auth.RegisterAsync(request.Email, request.Password, request.DisplayName, ct);
        return Accepted();
    }

    [HttpPost("confirm-email")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ConfirmEmail(ConfirmEmailRequest request, CancellationToken ct)
    {
        await auth.ConfirmEmailAsync(request.Token, ct);
        return NoContent();
    }

    /// <summary>Всегда 202: ответ не выдаёт, зарегистрирован ли email и подтверждён ли он.</summary>
    [HttpPost("resend-confirmation")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ResendConfirmation(ResendConfirmationRequest request, CancellationToken ct)
    {
        await auth.ResendConfirmationAsync(request.Email, ct);
        return Accepted();
    }

    [HttpPost("login")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    public async Task<LoginResponse> Login(LoginRequest request, CancellationToken ct)
    {
        var token = await auth.LoginAsync(request.Email, request.Password, ct);
        return new LoginResponse(token.Value, token.ExpiresAt);
    }
}
