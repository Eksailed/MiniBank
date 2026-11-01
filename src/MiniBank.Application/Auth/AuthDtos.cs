namespace MiniBank.Application.Auth;

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

public sealed record UserProfileDto(Guid Id, string Email, string DisplayName);
