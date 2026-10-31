using MiniBank.Domain;

namespace MiniBank.Application.Abstractions;

public interface ITransferRepository
{
    void Add(Transfer transfer);
}
