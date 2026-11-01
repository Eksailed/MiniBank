using Microsoft.AspNetCore.Mvc;
using MiniBank.Api.Auth;
using MiniBank.Application;

namespace MiniBank.Api.Controllers;

[ApiController]
[Route("accounts")]
public class AccountsController(AccountsService accounts, TransfersService transfers) : IdempotentControllerBase
{
    [HttpPost]
    [ProducesResponseType<AccountDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateAccountRequest request, CancellationToken ct)
    {
        var account = await accounts.CreateAsync(User.GetUserId(), request.Currency, ct);
        return CreatedAtAction(nameof(Get), new { id = account.Id }, account);
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AccountDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<AccountDto>> List(CancellationToken ct) =>
        await accounts.ListAsync(User.GetUserId(), ct);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<AccountDto>(StatusCodes.Status200OK)]
    public async Task<AccountDto> Get(Guid id, CancellationToken ct) =>
        await accounts.GetAsync(User.GetUserId(), id, ct);

    [HttpPost("{id:guid}/deposit")]
    [ProducesResponseType<TransferDto>(StatusCodes.Status200OK)]
    public async Task<TransferDto> Deposit(
        Guid id,
        DepositRequest request,
        [FromHeader(Name = IdempotencyHeaders.Key)] string? idempotencyKey,
        CancellationToken ct) =>
        Respond(await transfers.DepositAsync(
            User.GetUserId(), idempotencyKey, id, request.Amount, request.Currency, ct));

    [HttpGet("{id:guid}/entries")]
    [ProducesResponseType<IReadOnlyList<EntryDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<EntryDto>> Entries(
        Guid id, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        await accounts.GetEntriesAsync(User.GetUserId(), id, page, pageSize, ct);
}
