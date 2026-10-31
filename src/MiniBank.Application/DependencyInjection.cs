using Microsoft.Extensions.DependencyInjection;

namespace MiniBank.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AccountsService>();
        services.AddScoped<TransfersService>();
        return services;
    }
}
