using BuildSystem.Agent;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class IdentityProbeTokenLoaderTests
{
    private const string Canary = "private-token-path-and-read-error-canary";

    [Theory]
    [InlineData(1)]
    [InlineData(16384)]
    public void AcceptsActualBoundaryLengthsAndReturnsIndependentExactBytes(int length)
    {
        using var stream = new ProbeStream(length);
        var encrypted = IdentityProbeTokenLoader.ReadEncryptedToken(stream);
        Assert.Equal(16384, IdentityProbeTokenLoader.MaximumEncryptedBytes);
        Assert.Equal(length, encrypted.Length);
        Assert.All(encrypted, value => Assert.Equal((byte)0xA5, value));
        Assert.NotSame(stream.Scratch, encrypted);
        AssertCleanAndCallerOwned(stream);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16385)]
    public void RejectsActualEmptyAndOversizeLengthsWithFixedError(int length)
    {
        using var stream = new ProbeStream(length);
        Unavailable(() => IdentityProbeTokenLoader.ReadEncryptedToken(stream));
        Assert.Equal(length, stream.BytesRead);
        AssertCleanAndCallerOwned(stream);
    }

    [Fact]
    public void NonSeekableUnknownLengthNeverAccessesStreamMetadata()
    {
        using var stream = new ProbeStream(17);
        Assert.Equal(17, IdentityProbeTokenLoader.ReadEncryptedToken(stream).Length);
        Assert.Equal(0, stream.LengthReads);
        Assert.Equal(0, stream.CanSeekReads);
        AssertCleanAndCallerOwned(stream);
    }

    [Theory]
    [InlineData(1, 0L)]
    [InlineData(16384, long.MaxValue)]
    [InlineData(16385, 1L)]
    [InlineData(0, 16384L)]
    public void AdvertisedLengthCannotOverrideActualBytes(int actual, long advertised)
    {
        using var stream = new ProbeStream(actual) { AdvertisedLength = advertised };
        if (actual is > 0 and <= 16384)
            Assert.Equal(actual, IdentityProbeTokenLoader.ReadEncryptedToken(stream).Length);
        else
            Unavailable(() => IdentityProbeTokenLoader.ReadEncryptedToken(stream));
        Assert.Equal(0, stream.LengthReads);
        Assert.Equal(0, stream.CanSeekReads);
        AssertCleanAndCallerOwned(stream);
    }

    [Fact]
    public void OneByteChunksReadThroughEofAtMaximumAcceptedLength()
    {
        using var stream = new ProbeStream(16384) { ChunkSize = 1 };
        var encrypted = IdentityProbeTokenLoader.ReadEncryptedToken(stream);
        Assert.Equal(16384, encrypted.Length);
        Assert.All(encrypted, value => Assert.Equal((byte)0xA5, value));
        Assert.Equal(16385, stream.ReadCalls);
        AssertCleanAndCallerOwned(stream);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void InfiniteInputStopsAfterExactlyOneByteBeyondLimit(int chunkSize)
    {
        using var stream = new ProbeStream(int.MaxValue) { ChunkSize = chunkSize };
        Unavailable(() => IdentityProbeTokenLoader.ReadEncryptedToken(stream));
        Assert.Equal(16385, stream.BytesRead);
        Assert.InRange(stream.ReadCalls, 1, 16385);
        AssertCleanAndCallerOwned(stream);
    }

    [Fact]
    public void ReadFailureAfterWritingErasesScratchAndDoesNotLeakInnerError()
    {
        using var stream = new ProbeStream(12) { ThrowAfterWrite = true };
        Unavailable(() => IdentityProbeTokenLoader.ReadEncryptedToken(stream));
        Assert.Equal(12, stream.BytesRead);
        AssertCleanAndCallerOwned(stream);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16386)]
    public void InvalidReadCountsAreSanitizedAndScratchIsErased(int reportedCount)
    {
        using var stream = new ProbeStream(12) { ReportedCount = reportedCount };
        Unavailable(() => IdentityProbeTokenLoader.ReadEncryptedToken(stream));
        AssertCleanAndCallerOwned(stream);
    }

    [Fact]
    public void NullStreamUsesTheSameFixedFailure() =>
        Unavailable(() => IdentityProbeTokenLoader.ReadEncryptedToken(null!));

    [Theory]
    [InlineData(null)]
    [InlineData("\0private-token-path-and-read-error-canary")]
    [InlineData("private-token-path-and-read-error-canary/missing.dpapi")]
    public void UnsupportedPlatformLoadRejectsWithoutRequiringValidPath(string? path)
    {
        if (OperatingSystem.IsWindows()) return;
        Unavailable(() => IdentityProbeTokenLoader.Load(path!));
    }

    private static void Unavailable(Action action)
    {
        var error = Assert.Throws<IdentityProbeTokenUnavailableException>(action);
        Assert.Equal("IdentityProbeTokenUnavailable", error.Message);
        Assert.Equal("IdentityProbeTokenUnavailable", error.ToString());
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Canary, error.ToString(), StringComparison.Ordinal);
    }

    private static void AssertCleanAndCallerOwned(ProbeStream stream)
    {
        Assert.False(stream.Disposed);
        Assert.NotNull(stream.Scratch);
        Assert.Equal(16385, stream.Scratch.Length);
        Assert.All(stream.Scratch, value => Assert.Equal((byte)0, value));
    }

    private sealed class ProbeStream(int length) : Stream
    {
        public byte[]? Scratch { get; private set; }
        public int BytesRead { get; private set; }
        public int ReadCalls { get; private set; }
        public int LengthReads { get; private set; }
        public int CanSeekReads { get; private set; }
        public bool Disposed { get; private set; }
        public int ChunkSize { get; init; } = int.MaxValue;
        public long? AdvertisedLength { get; init; }
        public bool ThrowAfterWrite { get; init; }
        public int? ReportedCount { get; init; }
        public override bool CanRead => !Disposed;
        public override bool CanWrite => false;
        public override bool CanSeek { get { CanSeekReads++; return AdvertisedLength.HasValue; } }
        public override long Length
        {
            get { LengthReads++; return AdvertisedLength ?? throw new NotSupportedException(Canary); }
        }
        public override long Position
        {
            get => throw new NotSupportedException(Canary);
            set => throw new NotSupportedException(Canary);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Scratch = buffer;
            // Terminate a broken implementation too, so an infinite-input regression cannot hang the suite.
            if (++ReadCalls > 16386) throw new IOException(Canary);
            var read = Math.Min(Math.Min(count, ChunkSize), length - BytesRead);
            Array.Fill(buffer, (byte)0xA5, offset, read);
            BytesRead += read;
            if (ThrowAfterWrite) throw new IOException(Canary);
            return ReportedCount ?? read;
        }

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException(Canary);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(Canary);
        public override void SetLength(long value) => throw new NotSupportedException(Canary);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(Canary);
    }
}
