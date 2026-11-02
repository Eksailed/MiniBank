using Microsoft.Extensions.Configuration;

namespace MiniBank.Infrastructure.Events;

public sealed class EventsOptions
{
    public bool Enabled { get; init; } = true;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int BatchSize { get; init; } = 50;
    public int MaxAttempts { get; init; } = 10;
    public int RetentionDays { get; init; } = 7;
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMinutes(5);

    public static EventsOptions Load(IConfiguration configuration)
    {
        var section = configuration.GetSection("Events");
        var defaults = new EventsOptions();
        var options = new EventsOptions
        {
            Enabled = section.GetValue("Enabled", defaults.Enabled),
            PollInterval = section.GetValue("PollInterval", defaults.PollInterval),
            BatchSize = section.GetValue("BatchSize", defaults.BatchSize),
            MaxAttempts = section.GetValue("MaxAttempts", defaults.MaxAttempts),
            RetentionDays = section.GetValue("RetentionDays", defaults.RetentionDays),
            StaleAfter = section.GetValue("StaleAfter", defaults.StaleAfter),
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Events:PollInterval must be positive.");
        if (BatchSize < 1)
            throw new InvalidOperationException("Events:BatchSize must be at least 1.");
        if (MaxAttempts < 1)
            throw new InvalidOperationException("Events:MaxAttempts must be at least 1.");
        if (RetentionDays < 1)
            throw new InvalidOperationException("Events:RetentionDays must be at least 1.");
        if (StaleAfter <= TimeSpan.Zero)
            throw new InvalidOperationException("Events:StaleAfter must be positive.");
    }
}
