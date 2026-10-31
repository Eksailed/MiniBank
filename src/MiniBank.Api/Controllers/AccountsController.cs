using Microsoft.AspNetCore.Mvc;
using MiniBank.Application;

namespace MiniBank.Api.Controllers;

[ApiController]
[Route("accounts")]
public class AccountsController(AccountsService accounts) : ControllerBase
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
}
