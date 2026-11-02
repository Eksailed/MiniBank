using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using MiniBank.Api;
using MiniBank.Api.Auth;
using MiniBank.Api.Errors;
using MiniBank.Application;
using MiniBank.Application.Abstractions;
using MiniBank.Application.Limits;
using MiniBank.Application.Statements;
using MiniBank.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("MiniBank")
    ?? throw new InvalidOperationException("Connection string 'MiniBank' is not configured.");
var jwt = JwtOptions.Load(builder.Configuration);
var authPermitLimit = builder.Configuration.GetValue("Auth:RateLimitPerMinute", 10);
if (authPermitLimit <= 0)
    throw new InvalidOperationException("Auth:RateLimitPerMinute must be positive.");

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Токен из POST /auth/login."
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});
builder.Services.AddHealthChecks().AddDbContextCheck<MiniBankDbContext>();
builder.Services.AddApplication(
    builder.Configuration.GetSection("Limits").Get<LimitsOptions>(),
    builder.Configuration.GetValue("Statements:MaxCsvRows", StatementCsv.MaxRows));
builder.Services.AddInfrastructure(connectionString, builder.Configuration);

builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<IAccessTokenIssuer, JwtTokenIssuer>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(jwt.GetKeyBytes()),
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        // Токены, выданные до смены пароля, больше не действуют.
        options.Events = new JwtBearerEvents { OnTokenValidated = SessionRevocation.CheckAsync };
    });
// Всё закрыто по умолчанию; открытое помечается явно ([AllowAnonymous] на /auth/*, AllowAnonymous() на /health).
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.Configure<ForwardedHeadersOptions>(options =>
    ForwardedHeadersSetup.Configure(options, builder.Configuration));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicies.Auth, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authPermitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<MiniBankDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
// Пустые ответы 401/403/429 превращаются в application/problem+json.
app.UseStatusCodePages();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();

app.Run();

public partial class Program;
