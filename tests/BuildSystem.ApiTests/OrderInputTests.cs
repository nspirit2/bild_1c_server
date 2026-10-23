using BuildSystem.Application;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class OrderInputTests
{
    private static CreateEnvironmentRequest Valid() => new(Guid.NewGuid(), "My base", "archive", new(Guid.NewGuid()), 7);
    [Fact]
    public void CanonicalHashIsStableAndPayloadSensitive()
    {
        var request = Valid();
        var first = OrderInput.Validate("key", request);
        Assert.Equal(first.PayloadHash, OrderInput.Validate("other-key", request).PayloadHash);
        Assert.NotEqual(first.PayloadHash, OrderInput.Validate("key", request with { DisplayName = "Different" }).PayloadHash);
        Assert.Equal(64, first.PayloadHash.Length);
    }
    [Theory]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData("non-ascii-ключ")]
    public void InvalidIdempotencyKeyIsRejected(string key)
    {
        Assert.Equal(400, Assert.Throws<RequestRejectedException>(() => OrderInput.Validate(key, Valid())).StatusCode);
    }
    [Fact]
    public void OnlyArchiveSourceIsSupportedInThisSlice()
    {
        Assert.Throws<RequestRejectedException>(() => OrderInput.Validate("key", Valid() with { Mode = "branch" }));
        Assert.Throws<RequestRejectedException>(() => OrderInput.Validate("key", Valid() with { Source = new(Guid.Empty) }));
        Assert.Throws<RequestRejectedException>(() => OrderInput.Validate("key", Valid() with { TtlDays = 0 }));
    }
    [Fact]
    public void DisplayNameIsNotAPathOrCommandAndRejectsControlCharacters()
    {
        Assert.Throws<RequestRejectedException>(() => OrderInput.Validate("key", Valid() with { DisplayName = "bad\nname" }));
        Assert.Throws<RequestRejectedException>(() => OrderInput.Validate("key", Valid() with { DisplayName = " space " }));
    }
}
// end
