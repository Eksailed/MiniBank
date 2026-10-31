using MiniBank.Application.Abstractions;
using MiniBank.Domain;

namespace MiniBank.Infrastructure.Persistence;

internal sealed class TransferRepository(MiniBankDbContext db) : ITransferRepository
{
    public void Add(Transfer transfer) => db.Transfers.Add(transfer);
}
