using MiniBank.Application.Auth;

namespace MiniBank.Infrastructure.Tests;

public class ConfirmationTokensTests
{
    [Fact]
    public void Generate_ReturnsBase64UrlOf32Bytes_AndLowercaseHexSha256()
    {
        var (raw, hash) = ConfirmationTokens.Generate();

        Assert.Equal(43, raw.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", raw);
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, ConfirmationTokens.Hash(raw));
    }

    [Fact]
    public void Generate_ReturnsDifferentTokensEachTime()
    {
        var first = ConfirmationTokens.Generate();
        var second = ConfirmationTokens.Generate();

        Assert.NotEqual(first.Raw, second.Raw);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Hash_MatchesKnownSha256Vector()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ConfirmationTokens.Hash("abc"));
    }
}
