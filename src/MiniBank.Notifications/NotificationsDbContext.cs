using Microsoft.EntityFrameworkCore;

namespace MiniBank.Notifications;

/// <summary>
/// База потребителя: схема <c>notifications</c> (в той же базе, что и банк, но без общих таблиц и со своей историей миграций).
/// </summary>
public class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public const string Schema = "notifications";

    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<ReceivedEvent> ReceivedEvents => Set<ReceivedEvent>();
    public DbSet<TelegramLink> TelegramLinks => Set<TelegramLink>();
    public DbSet<LinkCode> LinkCodes => Set<LinkCode>();
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<TelegramDelivery> TelegramDeliveries => Set<TelegramDelivery>();

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", Schema));

    public static DbContextOptions<NotificationsDbContext> CreateOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<NotificationsDbContext>();
        Configure(builder, connectionString);
        return builder.Options;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSnakeCaseNamingConvention();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_events");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).ValueGeneratedNever();
        });

        modelBuilder.Entity<ReceivedEvent>(e =>
        {
            e.ToTable("received_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Type).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.EventId, x.OwnerId }).IsUnique();
            e.HasIndex(x => x.OwnerId);
        });

        modelBuilder.Entity<TelegramLink>(e =>
        {
            e.ToTable("telegram_links");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).ValueGeneratedNever();
            e.HasIndex(x => x.ChatId).IsUnique();
        });

        modelBuilder.Entity<LinkCode>(e =>
        {
            e.ToTable("link_codes");
            e.HasKey(x => x.CodeHash);
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<NotificationSettings>(e =>
        {
            e.ToTable("notification_settings");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).ValueGeneratedNever();
        });

        modelBuilder.Entity<TelegramDelivery>(e =>
        {
            e.ToTable("telegram_deliveries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Text).HasMaxLength(500).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.HasOne<ReceivedEvent>().WithMany().HasForeignKey(x => x.ReceivedEventId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ReceivedEventId).IsUnique();
            e.HasIndex(x => x.NextAttemptAt).HasDatabaseName("ix_telegram_deliveries_pending")
                .HasFilter("status = 'Pending'");
        });
    }
}

/// <summary>Событие уже обработано; ключ — <c>eventId</c> из конверта.</summary>
public class ProcessedEvent
{
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>Уведомление владельцу: по записи на каждого владельца события.</summary>
public class ReceivedEvent
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string Type { get; set; } = "";
    public Guid OwnerId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
