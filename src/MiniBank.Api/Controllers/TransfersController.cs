using Microsoft.AspNetCore.Mvc;
using MiniBank.Application;

namespace MiniBank.Api.Controllers;

[ApiController]
[Route("transfers")]
public class TransfersController(TransfersService transfers) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<TransferDto>(StatusCodes.Status200OK)]
    public async Task<TransferDto> Create(TransferRequest request, CancellationToken ct) =>
        await transfers.TransferAsync(
            request.FromAccountId, request.ToAccountId, request.Amount, request.Currency, ct);
}
