using MiniBank.Application.Statements;
using MiniBank.Domain;

namespace MiniBank.Application.Abstractions;

public interface ILedgerQuery
{
    /// <summary>Проводки счёта: сначала новые (created_at по убыванию, затем id); страницы нумеруются с единицы.</summary>
    Task<IReadOnlyList<LedgerEntry>> GetEntriesAsync(
        Guid accountId, EntryFilter filter, int page, int pageSize, CancellationToken ct);

    /// <summary>Сколько проводок подходит под фильтр, но не больше <c>cap + 1</c>: хватает, чтобы понять, что потолок превышен.</summary>
    Task<int> CountAsync(Guid accountId, EntryFilter filter, int cap, CancellationToken ct);

    /// <summary>Все проводки по фильтру потоком, в том же порядке, что и <see cref="GetEntriesAsync"/>.</summary>
    IAsyncEnumerable<LedgerEntry> StreamAsync(Guid accountId, EntryFilter filter, CancellationToken ct);

    /// <summary>Сумма модулей отрицательных проводок счёта (исходящих переводов) с <c>created_at &gt;= since</c>.</summary>
    Task<long> GetSpentSinceAsync(Guid accountId, DateTimeOffset since, CancellationToken ct);
}
