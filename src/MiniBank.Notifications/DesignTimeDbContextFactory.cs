using Microsoft.EntityFrameworkCore.Design;

namespace MiniBank.Notifications;

/// <summary>Нужна только для `dotnet ef`; строка подключения совпадает с docker-compose.yml.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) =>
        new(NotificationsDbContext.CreateOptions("Host=localhost;Port=5432;Database=minibank;Username=postgres;Password=postgres"));
}
