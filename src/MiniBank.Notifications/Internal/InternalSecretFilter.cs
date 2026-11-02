using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using MiniBank.Notifications.Telegram;

namespace MiniBank.Notifications.Internal;

/// <summary>Пропускает только запросы с верным заголовком <c>X-Internal-Secret</c>.</summary>
public sealed class InternalSecretFilter(TelegramOptions options) : IEndpointFilter
{
    public const string HeaderName = "X-Internal-Secret";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        var expected = Encoding.UTF8.GetBytes(options.InternalSecret);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), expected))
            return ValueTask.FromResult<object?>(Results.Unauthorized());
        return next(context);
    }
}
