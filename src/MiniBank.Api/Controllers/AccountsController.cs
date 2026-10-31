using Microsoft.AspNetCore.Mvc;
using MiniBank.Application;

namespace MiniBank.Api.Controllers;

[ApiController]
[Route("accounts")]
public class AccountsController(AccountsService accounts, TransfersService transfers) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<AccountDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateAccountRequest request, CancellationToken ct)
    {
        var account = await accounts.CreateAsync(request.Owner, request.Currency, ct);
        return CreatedAtAction(nameof(Get), new { id = account.Id }, account);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<AccountDto>(StatusCodes.Status200OK)]
    public async Task<AccountDto> Get(Guid id, CancellationToken ct) => await accounts.GetAsync(id, ct);

    [HttpPost("{id:guid}/deposit")]
    [ProducesResponseType<TransferDto>(StatusCodes.Status200OK)]
    public async Task<TransferDto> Deposit(Guid id, DepositRequest request, CancellationToken ct) =>
        await transfers.DepositAsync(id, request.Amount, request.Currency, ct);

    [HttpGet("{id:guid}/entries")]
    [ProducesResponseType<IReadOnlyList<EntryDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<EntryDto>> Entries(
        Guid id, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        await accounts.GetEntriesAsync(id, page, pageSize, ct);
}
