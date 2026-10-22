using BuildSystem.Application;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class AgentTokenTests
{
    [Fact]
    public void TokensAreRandomOpaqueAndStoredOnlyAsDigest()
    {
        var first = TokenProtector.CreateToken();
        var second = TokenProtector.CreateToken();
        Assert.Equal(64, first.Length);
        Assert.NotEqual(first, second);
        Assert.NotEqual(first, TokenProtector.Hash(first));
        Assert.True(TokenProtector.Verify(first, TokenProtector.Hash(first)));
        Assert.False(TokenProtector.Verify(second, TokenProtector.Hash(first)));
    }
    [Theory]
    [InlineData("")]
    [InlineData("not-a-hex-digest")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void InvalidDigestsFailClosed(string hash) => Assert.False(TokenProtector.Verify("token", hash));
}
// end
