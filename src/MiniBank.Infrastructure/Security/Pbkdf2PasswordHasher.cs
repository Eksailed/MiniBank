using Microsoft.AspNetCore.Identity;
using MiniBank.Application.Abstractions;

namespace MiniBank.Infrastructure.Security;

/// <summary>PBKDF2 с солью и числом итераций внутри хэша — готовая реализация из Microsoft.Extensions.Identity.Core.</summary>
internal sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private static readonly object Subject = new();

    private readonly PasswordHasher<object> inner = new();

    public Pbkdf2PasswordHasher()
    {
        DummyHash = inner.HashPassword(Subject, Guid.NewGuid().ToString("N"));
    }

    public string DummyHash { get; }

    public string Hash(string password) => inner.HashPassword(Subject, password);

    public bool Verify(string hash, string password) =>
        inner.VerifyHashedPassword(Subject, hash, password) != PasswordVerificationResult.Failed;
}
