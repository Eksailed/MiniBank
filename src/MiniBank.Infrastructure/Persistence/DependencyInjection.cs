using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MiniBank.Application.Abstractions;
using MiniBank.Application.Auth;
using MiniBank.Infrastructure.Email;
using MiniBank.Infrastructure.Security;

namespace MiniBank.Infrastructure.Persistence;

public static class DependencyInjection
{
    /// <param name="configuration">
    /// Конфигурация приложения. Без неё (тесты БД) SMTP и адрес веб-клиента остаются значениями по умолчанию;
    /// с ней <c>Web:BaseUrl</c> обязателен — без него ссылка из письма будет вести в никуда.
    /// </param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, string connectionString, IConfiguration? configuration = null)
    {
        services.AddDbContext<MiniBankDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<ITransferRepository, TransferRepository>();
        services.AddScoped<ILedgerQuery, LedgerQuery>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton(TimeProvider.System);

        var smtp = configuration?.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions();
        services.AddSingleton(smtp);
        services.AddSingleton<IEmailSender, MailKitEmailSender>();
        services.AddSingleton(new AuthOptions { WebBaseUrl = ReadWebBaseUrl(configuration) });
        return services;
    }

    private static string ReadWebBaseUrl(IConfiguration? configuration)
    {
        if (configuration is null)
            return string.Empty;

        var baseUrl = configuration["Web:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Web:BaseUrl is required: the confirmation link in the email points to it.");
        return baseUrl.Trim().TrimEnd('/');
    }
}
