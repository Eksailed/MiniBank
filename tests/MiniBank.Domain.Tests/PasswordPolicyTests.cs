using MiniBank.Domain;

namespace MiniBank.Domain.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(128)]
    public void Validate_AcceptsBoundaryLengths(int length)
    {
        PasswordPolicy.Validate(new string('x', length));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(129)]
    public void Validate_RejectsOutOfRange(int length)
    {
        Assert.Throws<DomainValidationException>(() => PasswordPolicy.Validate(new string('x', length)));
    }

    [Fact]
    public void Validate_RejectsNull()
    {
        Assert.Throws<DomainValidationException>(() => PasswordPolicy.Validate(null));
    }

    [Fact]
    public void Validate_DoesNotTrimWhitespace()
    {
        PasswordPolicy.Validate(new string(' ', 8));
    }
}
