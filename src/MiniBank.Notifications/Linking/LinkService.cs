using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;

namespace MiniBank.Notifications.Linking;

public sealed record LinkCodeResult(string Code, DateTimeOffset ExpiresAt);

public sealed record NotificationSettingsDto(bool Enabled, bool OnTransferSent, bool OnTransferReceived, bool OnDeposit);

public sealed record TelegramStatus(bool Linked, DateTimeOffset? LinkedAt, NotificationSettingsDto Settings);

/// <summary>Привязка Telegram-чата к пользователю по одноразовому коду и настройки уведомлений.</summary>
public sealed class LinkService(IDbContextFactory<NotificationsDbContext> dbFactory, TimeProvider clock)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private const string RelinkedError = "chat relinked";

    private static readonly NotificationSettingsDto Defaults = new(true, true, true, true);

    public static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    /// <summary>Выпускает новый код и аннулирует прежние коды пользователя.</summary>
    public async Task<LinkCodeResult> CreateCodeAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var code = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        var expiresAt = now + CodeLifetime;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Параллельные запросы одного пользователя выстраиваются в очередь: активным остаётся ровно один код.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({userId.ToString()}, 0))", ct);
        await db.LinkCodes.Where(c => c.UserId == userId || c.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        db.LinkCodes.Add(new LinkCode { CodeHash = Hash(code), UserId = userId, ExpiresAt = expiresAt });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new LinkCodeResult(code, expiresAt);
    }

    /// <summary>Погашает код и привязывает чат; чат, ранее принадлежавший другому пользователю, переходит к новому.</summary>
    public async Task<bool> RedeemAsync(string code, long chatId, CancellationToken ct)
    {
        code = code.Trim();
        if (code.Length == 0)
            return false;

        var now = clock.GetUtcNow();
        var hash = Hash(code);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var userId = await db.LinkCodes.Where(c => c.CodeHash == hash && c.ExpiresAt > now)
            .Select(c => (Guid?)c.UserId).FirstOrDefaultAsync(ct);
        if (userId is null)
            return false;
        // Удаление — точка одноразовости: параллельное погашение того же кода получит 0 строк.
        if (await db.LinkCodes.Where(c => c.CodeHash == hash).ExecuteDeleteAsync(ct) == 0)
            return false;

        var previousOwner = await db.TelegramLinks.Where(l => l.ChatId == chatId)
            .Select(l => (Guid?)l.UserId).FirstOrDefaultAsync(ct);
        if (previousOwner is not null && previousOwner != userId)
            await FailPendingAsync(db, chatId, now, ct);
        var previousChat = await db.TelegramLinks.Where(l => l.UserId == userId)
            .Select(l => (long?)l.ChatId).FirstOrDefaultAsync(ct);
        if (previousChat is not null && previousChat != chatId)
            await FailPendingAsync(db, previousChat.Value, now, ct);

        await db.TelegramLinks.Where(l => l.ChatId == chatId || l.UserId == userId).ExecuteDeleteAsync(ct);
        db.TelegramLinks.Add(new TelegramLink { UserId = userId.Value, ChatId = chatId, LinkedAt = now });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<TelegramStatus> GetStatusAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var linkedAt = await db.TelegramLinks.Where(l => l.UserId == userId)
            .Select(l => (DateTimeOffset?)l.LinkedAt).FirstOrDefaultAsync(ct);
        var settings = await db.NotificationSettings.Where(s => s.UserId == userId)
            .Select(s => new NotificationSettingsDto(s.Enabled, s.OnTransferSent, s.OnTransferReceived, s.OnDeposit))
            .FirstOrDefaultAsync(ct);
        return new TelegramStatus(linkedAt is not null, linkedAt, settings ?? Defaults);
    }

    public async Task UnlinkAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var chatId = await db.TelegramLinks.Where(l => l.UserId == userId)
            .Select(l => (long?)l.ChatId).FirstOrDefaultAsync(ct);
        if (chatId is not null)
        {
            await FailPendingAsync(db, chatId.Value, now, ct);
            await db.TelegramLinks.Where(l => l.UserId == userId).ExecuteDeleteAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task UpdateSettingsAsync(Guid userId, NotificationSettingsDto settings, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.NotificationSettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (row is null)
        {
            row = new NotificationSettings { UserId = userId };
            db.NotificationSettings.Add(row);
        }
        row.Enabled = settings.Enabled;
        row.OnTransferSent = settings.OnTransferSent;
        row.OnTransferReceived = settings.OnTransferReceived;
        row.OnDeposit = settings.OnDeposit;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Закрывает неотправленные сообщения чата, чтобы они не ушли новому владельцу.</summary>
    private static Task<int> FailPendingAsync(NotificationsDbContext db, long chatId, DateTimeOffset now, CancellationToken ct) =>
        db.TelegramDeliveries.Where(d => d.ChatId == chatId && d.Status == DeliveryStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DeliveryStatus.Failed)
                .SetProperty(d => d.NextAttemptAt, now)
                .SetProperty(d => d.LastError, RelinkedError), ct);
}
