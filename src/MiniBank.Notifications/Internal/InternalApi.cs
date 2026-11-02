using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MiniBank.Notifications.Linking;
using MiniBank.Notifications.Telegram;

namespace MiniBank.Notifications.Internal;

public static class InternalApi
{
    /// <summary>Внутренний HTTP API для сервиса API: привязка Telegram и настройки уведомлений пользователя.</summary>
    public static IEndpointRouteBuilder MapInternalApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/users/{userId:guid}/telegram").AddEndpointFilter<InternalSecretFilter>();

        group.MapGet("", async (Guid userId, LinkService links, CancellationToken ct) =>
            Results.Ok(await links.GetStatusAsync(userId, ct)));

        group.MapPost("link-code", async (Guid userId, LinkService links, TelegramOptions options, CancellationToken ct) =>
        {
            var issued = await links.CreateCodeAsync(userId, ct);
            return Results.Ok(new
            {
                url = $"https://t.me/{options.BotUsername}?start={issued.Code}",
                expiresAt = issued.ExpiresAt,
            });
        });

        group.MapDelete("", async (Guid userId, LinkService links, CancellationToken ct) =>
        {
            await links.UnlinkAsync(userId, ct);
            return Results.NoContent();
        });

        group.MapPut("settings", async (Guid userId, NotificationSettingsDto settings, LinkService links, CancellationToken ct) =>
        {
            await links.UpdateSettingsAsync(userId, settings, ct);
            return Results.NoContent();
        });

        return app;
    }
}
