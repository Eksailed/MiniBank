using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniBank.Api.Auth;
using MiniBank.Application.Auth;

namespace MiniBank.Api.Controllers;

[ApiController]
[Route("me")]
[Authorize]
public class MeController(AuthService auth) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<UserProfileDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserProfileDto>> Get(CancellationToken ct) =>
        await auth.GetProfileAsync(User.GetUserId(), ct) is { } profile ? profile : Unauthorized();
}
