using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MiniBank.Infrastructure.Persistence;

/// <summary>Нужна только для `dotnet ef`; строка подключения совпадает с docker-compose.yml.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MiniBankDbContext>
{
    public MiniBankDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MiniBankDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=minibank;Username=postgres;Password=postgres")
            .Options;
        return new MiniBankDbContext(options);
    }
}
