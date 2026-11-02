using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniBank.Api.Auth;
using MiniBank.Application;
using MiniBank.Application.Abstractions;

namespace MiniBank.Api.Controllers;

[ApiController]
[Route("me/notifications")]
[Authorize]
public class NotificationsController(INotificationsGateway notifications) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<TelegramStatusDto>(StatusCodes.Status200OK)]
    public async Task<TelegramStatusDto> Get(CancellationToken ct) =>
        await notifications.GetAsync(User.GetUserId(), ct);

    [HttpPost("telegram/link")]
    [ProducesResponseType<LinkDto>(StatusCodes.Status200OK)]
    public async Task<LinkDto> CreateLink(CancellationToken ct) =>
        await notifications.CreateLinkAsync(User.GetUserId(), ct);

    [HttpDelete("telegram")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Unlink(CancellationToken ct)
    {
        await notifications.UnlinkAsync(User.GetUserId(), ct);
        return NoContent();
    }

    [HttpPut("settings")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateSettings(UpdateNotificationSettingsRequest request, CancellationToken ct)
    {
        await notifications.UpdateSettingsAsync(User.GetUserId(),
            new NotificationSettingsDto(request.Enabled, request.OnTransferSent, request.OnTransferReceived, request.OnDeposit), ct);
        return NoContent();
    }
}
