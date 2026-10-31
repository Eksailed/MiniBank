using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniBank.Domain;

namespace MiniBank.Infrastructure.Persistence.Configurations;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    private static readonly DateTimeOffset SeededAt = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts", table =>
            table.HasCheckConstraint("ck_accounts_balance_non_negative", "balance >= 0 OR is_system"));

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Owner).HasMaxLength(Account.MaxOwnerLength).IsRequired();
        builder.Property(a => a.Currency).HasMaxLength(3).IsRequired();
        builder.Property(a => a.Version).IsConcurrencyToken();

        // Касса по одной на каждую поддерживаемую валюту; Id совпадают с SystemAccounts.CashId.
        builder.HasData(Currencies.Supported.Select(currency => new
        {
            Id = SystemAccounts.CashId(currency),
            Owner = $"Cash {currency}",
            Currency = currency,
            Balance = 0L,
            Version = 0,
            Status = AccountStatus.Active,
            IsSystem = true,
            CreatedAt = SeededAt
        }));
    }
}
