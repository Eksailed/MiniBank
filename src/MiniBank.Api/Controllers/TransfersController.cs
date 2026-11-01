using Microsoft.AspNetCore.Mvc;
using MiniBank.Api.Auth;
using MiniBank.Application;

namespace MiniBank.Api.Controllers;

[ApiController]
[Route("transfers")]
public class TransfersController(TransfersService transfers) : IdempotentControllerBase
{
    [HttpPost]
    [ProducesResponseType<TransferDto>(StatusCodes.Status200OK)]
    public async Task<TransferDto> Create(
        TransferRequest request,
        [FromHeader(Name = IdempotencyHeaders.Key)] string? idempotencyKey,
        CancellationToken ct) =>
        Respond(await transfers.TransferAsync(
            User.GetUserId(), idempotencyKey, request.FromAccountId, request.ToAccountId, request.Amount, request.Currency, ct));
}
