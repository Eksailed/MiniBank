using MiniBank.Domain;

namespace MiniBank.Domain.Tests;

public class EmailAddressTests
{
    [Fact]
    public void Normalize_TrimsAndLowercases()
    {
        Assert.Equal("alice@example.com", EmailAddress.Normalize(" Alice@Example.COM "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("a@")]
    [InlineData("@b.c")]
    [InlineData("a@b")]
    [InlineData("a b@c.d")]
    [InlineData("a@@b.c")]
    [InlineData("a@b .c")]
    public void Normalize_RejectsInvalid(string? email)
    {
        Assert.Throws<DomainValidationException>(() => EmailAddress.Normalize(email));
    }

    [Fact]
    public void Normalize_Accepts254Characters_RejectsMore()
    {
        var domain = "@example.com";
        var ok = new string('a', 254 - domain.Length) + domain;

        Assert.Equal(254, EmailAddress.Normalize(ok).Length);
        Assert.Throws<DomainValidationException>(() => EmailAddress.Normalize("a" + ok));
    }
}
