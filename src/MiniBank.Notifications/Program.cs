using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MiniBank.Notifications;
using MiniBank.Notifications.Internal;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddNotifications(builder.Configuration);
builder.Services.AddTelegram(builder.Configuration);

var app = builder.Build();
app.MapInternalApi();

if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
    await app.Services.MigrateNotificationsAsync();

await app.RunAsync();
