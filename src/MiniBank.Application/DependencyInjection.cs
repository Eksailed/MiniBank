using Microsoft.Extensions.DependencyInjection;
using MiniBank.Application.Auth;
using MiniBank.Application.Limits;
using MiniBank.Application.Statements;

namespace MiniBank.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services, LimitsOptions? limits = null, int maxCsvRows = StatementCsv.MaxRows)
    {
        services.AddSingleton(new StatementOptions(maxCsvRows));
        services.AddSingleton(new LimitPolicy(limits ?? LimitsOptions.Builtin));
        services.AddScoped<AccountsService>();
        services.AddScoped<AuthService>();
        services.AddScoped<IdempotencyExecutor>();
        services.AddScoped<TransfersService>();
        return services;
    }
}
