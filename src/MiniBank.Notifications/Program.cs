using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MiniBank.Notifications;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddNotifications(builder.Configuration);

var host = builder.Build();

if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
    await host.Services.MigrateNotificationsAsync();

await host.RunAsync();
