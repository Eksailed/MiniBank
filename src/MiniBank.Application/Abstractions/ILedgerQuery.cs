using MiniBank.Domain;

namespace MiniBank.Application.Abstractions;

public interface ILedgerQuery
{
    /// <summary>Проводки счёта: сначала новые (created_at по убыванию, затем id); страницы нумеруются с единицы.</summary>
    Task<IReadOnlyList<LedgerEntry>> GetEntriesAsync(Guid accountId, int page, int pageSize, CancellationToken ct);

    /// <summary>Сумма модулей отрицательных проводок счёта (исходящих переводов) с <c>created_at &gt;= since</c>.</summary>
    Task<long> GetSpentSinceAsync(Guid accountId, DateTimeOffset since, CancellationToken ct);
}
