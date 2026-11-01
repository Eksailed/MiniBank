using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using MiniBank.Api.Auth;
using MiniBank.Application;
using MiniBank.Application.Statements;
using MiniBank.Domain;

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

    [HttpGet("{id:guid}/limits")]
    [ProducesResponseType<LimitsDto>(StatusCodes.Status200OK)]
    public async Task<LimitsDto> GetLimits(Guid id, CancellationToken ct) =>
        await accounts.GetLimitsAsync(User.GetUserId(), id, ct);

    [HttpPut("{id:guid}/limits")]
    [ProducesResponseType<LimitsDto>(StatusCodes.Status200OK)]
    public async Task<LimitsDto> SetLimits(Guid id, UpdateLimitsRequest request, CancellationToken ct) =>
        await accounts.SetLimitsAsync(User.GetUserId(), id, request.PerOperation, request.Daily, ct);

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
        Guid id,
        CancellationToken ct,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string? direction = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50) =>
        await accounts.GetEntriesAsync(User.GetUserId(), id, ParseFilter(from, to, direction), page, pageSize, ct);

    /// <summary>Выписка в CSV (UTF-8 с BOM, разделитель «;»): те же фильтры, без пагинации, пишется потоком.</summary>
    [HttpGet("{id:guid}/entries.csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task EntriesCsv(
        Guid id,
        CancellationToken ct,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string? direction = null)
    {
        // Владение, фильтр и потолок строк проверяются до первого байта: ошибка остаётся обычным ответом с кодом.
        var (currency, rows) = await accounts.OpenStatementAsync(
            User.GetUserId(), id, ParseFilter(from, to, direction), ct);

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"statement-{id}.csv\"";
        await Response.Body.WriteAsync(Encoding.UTF8.GetPreamble(), ct);
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(false), leaveOpen: true);
        await StatementCsv.WriteAsync(writer, currency, rows, ct);
    }

    /// <summary>Даты по ISO 8601; без смещения считаются UTC. Пустое значение — как отсутствие параметра.</summary>
    private static EntryFilter ParseFilter(string? from, string? to, string? direction) =>
        new(ParseInstant(from, "from"), ParseInstant(to, "to"), ParseDirection(direction));

    // Только ISO 8601: дата, либо дата и время с необязательными долями секунды и смещением (Z, +03:00 или без него — UTC).
    private static readonly string[] IsoFormats =
        ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK"];

    private static DateTimeOffset? ParseInstant(string? value, string name)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (DateTimeOffset.TryParseExact(
                value, IsoFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed;
        throw new DomainValidationException($"'{name}' must be an ISO 8601 date or date-time.");
    }

    private static EntryDirection? ParseDirection(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" => null,
        "in" => EntryDirection.In,
        "out" => EntryDirection.Out,
        _ => throw new DomainValidationException("'direction' must be 'in' or 'out'.")
    };
}
