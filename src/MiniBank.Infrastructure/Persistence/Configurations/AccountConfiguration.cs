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
        {
            table.HasCheckConstraint("ck_accounts_balance_non_negative", "balance >= 0 OR is_system");
            // В миграции добавляется как NOT VALID: старые счета без владельца остаются, новые — нет.
            table.HasCheckConstraint("ck_accounts_owner_required", "is_system OR user_id IS NOT NULL");
            table.HasCheckConstraint("ck_accounts_per_operation_limit_positive", "per_operation_limit > 0");
            table.HasCheckConstraint("ck_accounts_daily_limit_positive", "daily_limit > 0");
            // Тоже NOT VALID в миграции: счета, открытые до лимитов, остаются без них, новые — нет.
            table.HasCheckConstraint("ck_accounts_limits_required",
                "is_system OR (per_operation_limit IS NOT NULL AND daily_limit IS NOT NULL)");
        });

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Owner).HasMaxLength(Account.MaxOwnerLength).IsRequired();
        builder.Property(a => a.Currency).HasMaxLength(3).IsRequired();
        builder.Property(a => a.Version).IsConcurrencyToken();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => a.UserId).HasDatabaseName("ix_accounts_user_id");

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
