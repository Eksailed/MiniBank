using MiniBank.Domain;

namespace MiniBank.Application.Abstractions;

public interface ILedgerQuery
{
    /// <summary>Проводки счёта: сначала новые (created_at по убыванию, затем id); страницы нумеруются с единицы.</summary>
    Task<IReadOnlyList<LedgerEntry>> GetEntriesAsync(Guid accountId, int page, int pageSize, CancellationToken ct);
}
