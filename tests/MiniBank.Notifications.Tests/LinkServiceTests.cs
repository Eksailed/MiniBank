using Microsoft.EntityFrameworkCore;
using MiniBank.Notifications.Linking;
using MiniBank.TestSupport;

namespace MiniBank.Notifications.Tests;

[Collection("postgres")]
public class LinkServiceTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class Factory(string connectionString) : IDbContextFactory<NotificationsDbContext>
    {
        public NotificationsDbContext CreateDbContext() => new(NotificationsDbContext.CreateOptions(connectionString));
    }

    private async Task<(Factory Db, FakeClock Clock, LinkService Service)> SetupAsync()
    {
        var db = new Factory(await postgres.CreateDatabaseAsync());
        await using (var context = db.CreateDbContext())
            await context.Database.MigrateAsync();
        var clock = new FakeClock(Start);
        return (db, clock, new LinkService(db, clock));
    }

    [Fact]
    public async Task CreateCode_StoresOnlyHash()
    {
        var (db, _, service) = await SetupAsync();

        var issued = await service.CreateCodeAsync(Guid.NewGuid(), default);

        await using var context = db.CreateDbContext();
        var stored = await context.LinkCodes.SingleAsync();
        Assert.NotEqual(issued.Code, stored.CodeHash);
        Assert.Equal(LinkService.Hash(issued.Code), stored.CodeHash);
        Assert.Equal(Start.AddMinutes(10), issued.ExpiresAt);
        Assert.True(issued.Code.Length >= 22);
    }

    [Fact]
    public async Task Redeem_CreatesLinkAndConsumesCode()
    {
        var (db, _, service) = await SetupAsync();
        var user = Guid.NewGuid();
        var issued = await service.CreateCodeAsync(user, default);

        Assert.True(await service.RedeemAsync(issued.Code, 555, default));

        await using var context = db.CreateDbContext();
        var link = await context.TelegramLinks.SingleAsync();
        Assert.Equal((user, 555L), (link.UserId, link.ChatId));
        Assert.Empty(await context.LinkCodes.ToListAsync());
    }

    [Fact]
    public async Task Redeem_SecondUse_Fails()
    {
        var (_, _, service) = await SetupAsync();
        var issued = await service.CreateCodeAsync(Guid.NewGuid(), default);

        Assert.True(await service.RedeemAsync(issued.Code, 1, default));
        Assert.False(await service.RedeemAsync(issued.Code, 2, default));
    }

    [Fact]
    public async Task Redeem_AfterExpiry_Fails()
    {
        var (db, clock, service) = await SetupAsync();
        var issued = await service.CreateCodeAsync(Guid.NewGuid(), default);
        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.False(await service.RedeemAsync(issued.Code, 1, default));

        await using var context = db.CreateDbContext();
        Assert.Empty(await context.TelegramLinks.ToListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не-код")]
    public async Task Redeem_UnknownCode_ReturnsFalse(string code)
    {
        var (_, _, service) = await SetupAsync();
        Assert.False(await service.RedeemAsync(code, 1, default));
    }

    [Fact]
    public async Task NewCode_InvalidatesPreviousOne()
    {
        var (_, _, service) = await SetupAsync();
        var user = Guid.NewGuid();
        var first = await service.CreateCodeAsync(user, default);
        var second = await service.CreateCodeAsync(user, default);

        Assert.False(await service.RedeemAsync(first.Code, 1, default));
        Assert.True(await service.RedeemAsync(second.Code, 1, default));
    }

    [Fact]
    public async Task Redeem_ChatOfAnotherUser_MovesLinkToNewUser()
    {
        var (db, _, service) = await SetupAsync();
        Guid old = Guid.NewGuid(), current = Guid.NewGuid();
        Assert.True(await service.RedeemAsync((await service.CreateCodeAsync(old, default)).Code, 77, default));

        Assert.True(await service.RedeemAsync((await service.CreateCodeAsync(current, default)).Code, 77, default));

        await using var context = db.CreateDbContext();
        var link = await context.TelegramLinks.SingleAsync();
        Assert.Equal(current, link.UserId);
        Assert.False((await service.GetStatusAsync(old, default)).Linked);
    }

    [Fact]
    public async Task Redeem_NewChatOfSameUser_ReplacesLink()
    {
        var (db, _, service) = await SetupAsync();
        var user = Guid.NewGuid();
        await service.RedeemAsync((await service.CreateCodeAsync(user, default)).Code, 1, default);

        Assert.True(await service.RedeemAsync((await service.CreateCodeAsync(user, default)).Code, 2, default));

        await using var context = db.CreateDbContext();
        Assert.Equal(2, (await context.TelegramLinks.SingleAsync()).ChatId);
    }

    [Fact]
    public async Task Status_WithoutRows_ReturnsDefaults()
    {
        var (_, _, service) = await SetupAsync();

        var status = await service.GetStatusAsync(Guid.NewGuid(), default);

        Assert.False(status.Linked);
        Assert.Null(status.LinkedAt);
        Assert.Equal(new NotificationSettingsDto(true, true, true, true), status.Settings);
    }

    [Fact]
    public async Task UpdateSettings_PersistsAndUnlinkRemovesLink()
    {
        var (_, _, service) = await SetupAsync();
        var user = Guid.NewGuid();
        await service.RedeemAsync((await service.CreateCodeAsync(user, default)).Code, 1, default);

        await service.UpdateSettingsAsync(user, new NotificationSettingsDto(true, false, true, false), default);
        await service.UpdateSettingsAsync(user, new NotificationSettingsDto(false, false, true, false), default);
        var linked = await service.GetStatusAsync(user, default);
        await service.UnlinkAsync(user, default);
        var unlinked = await service.GetStatusAsync(user, default);

        Assert.True(linked.Linked);
        Assert.Equal(Start, linked.LinkedAt);
        Assert.Equal(new NotificationSettingsDto(false, false, true, false), linked.Settings);
        Assert.False(unlinked.Linked);
        Assert.Equal(new NotificationSettingsDto(false, false, true, false), unlinked.Settings);
    }
}
