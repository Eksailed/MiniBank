using Microsoft.AspNetCore.Mvc;
using MiniBank.Application;

namespace MiniBank.Api.Controllers;

public static class IdempotencyHeaders
{
    public const string Key = "Idempotency-Key";
    public const string Replayed = "Idempotent-Replayed";
}

/// <summary>Базовый контроллер для эндпоинтов, принимающих Idempotency-Key.</summary>
public abstract class IdempotentControllerBase : ControllerBase
{
    /// <summary>Возвращает значение и помечает ответ-повтор заголовком Idempotent-Replayed.</summary>
    protected T Respond<T>(IdempotentResult<T> result)
    {
        if (result.Replayed)
            Response.Headers[IdempotencyHeaders.Replayed] = "true";
        return result.Value;
    }
}
