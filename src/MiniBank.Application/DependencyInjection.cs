using Microsoft.Extensions.DependencyInjection;
using MiniBank.Application.Auth;

namespace MiniBank.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AccountsService>();
        services.AddScoped<AuthService>();
        services.AddScoped<IdempotencyExecutor>();
        services.AddScoped<TransfersService>();
        return services;
    }
}
