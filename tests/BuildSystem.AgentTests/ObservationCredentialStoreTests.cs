using System.Security.Cryptography;
using System.Text;
using BuildSystem.Agent;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class ObservationCredentialStoreTests
{
    private const string Secret = "test-credential-not-for-production";
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    [Fact]
    public void PureRoundTripAndRedactedRepresentation()
    {
        var bytes = ObservationCredentialStore.Encode("operator", Secret);
        try
        {
            var parsed = ObservationCredentialStore.Parse(bytes);
            Assert.Equal("operator", parsed.ClusterUser);
            Assert.Equal(Secret, parsed.ClusterPassword);
            Assert.DoesNotContain(Secret, parsed.ToString(), StringComparison.Ordinal);
            Assert.Contains("\"version\":1", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"version\":1,\"clusterUser\":\"u\"}")]
    [InlineData("{\"version\":1,\"clusterPassword\":\"p\"}")]
    [InlineData("{\"clusterUser\":\"u\",\"clusterPassword\":\"p\"}")]
    [InlineData("{\"version\":2,\"clusterUser\":\"u\",\"clusterPassword\":\"p\"}")]
    [InlineData("{\"version\":\"1\",\"clusterUser\":\"u\",\"clusterPassword\":\"p\"}")]
    [InlineData("{\"version\":1.0,\"clusterUser\":\"u\",\"clusterPassword\":\"p\"}")]
    [InlineData("{\"version\":1,\"clusterUser\":null,\"clusterPassword\":\"p\"}")]
    [InlineData("{\"version\":1,\"clusterUser\":\"\",\"clusterPassword\":\"p\"}")]
    [InlineData("{\"version\":1,\"clusterUser\":\"u\",\"clusterPassword\":\"\"}")]
    [InlineData("{\"version\":1,\"clusterUser\":\"u\",\"clusterPassword\":\"p\",\"extra\":1}")]
    [InlineData("{\"version\":1,\"clusterUser\":\"u\",\"clusterPassword\":\"p\",\"clusterPassword\":\"q\"}")]
    [InlineData("{\"version\":1,\"clusterUser\":\"u\",\"clusterPassword\":\"p\",\"ClusterPassword\":\"q\"}")]
    [InlineData("{\"version\":1,\"clusterUser\":\"u\",\"clusterPassword\":\"p\"}{}")]
    [InlineData("{\"version\":1,\"clusterUser\":\"u\\n\",\"clusterPassword\":\"p\"}")]
    [InlineData("{\"version\":1,\"clusterUser\":\"u\",\"clusterPassword\":{\"nested\":\"p\"}}")]
    public void RejectsMalformedOrNonExactSchema(string json) => AssertRedacted(() => ObservationCredentialStore.Parse(Utf8(json)));

    [Fact]
    public void RejectsInvalidUtf8TooLargeAndInvalidJson()
    {
        AssertRedacted(() => ObservationCredentialStore.Parse(new byte[] { 0xFF }));
        AssertRedacted(() => ObservationCredentialStore.Parse(new byte[ObservationCredentialStore.MaxPlaintextBytes + 1]));
        AssertRedacted(() => ObservationCredentialStore.Parse(Utf8("{\"version\":1,")));
        AssertRedacted(() => ObservationCredentialStore.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void RejectsLongAndControlCredentialsInBothDirections()
    {
        foreach (var (user, password) in new[] { (new string('u', 129), "p"), ("u", new string('p', 257)),
                     ("u\0", "p"), ("u", "p\n"), ("", "p"), ("u", "") })
        {
            AssertRedacted(() => ObservationCredentialStore.Encode(user, password));
            var json = "{\"version\":1,\"clusterUser\":" + System.Text.Json.JsonSerializer.Serialize(user) +
                       ",\"clusterPassword\":" + System.Text.Json.JsonSerializer.Serialize(password) + "}";
            AssertRedacted(() => ObservationCredentialStore.Parse(Utf8(json)));
        }
    }

    [Fact]
    public void ExactCredentialBoundsAndNonAsciiRoundTrip()
    {
        var user = new string('я', 128);
        var password = new string('界', 256);
        var bytes = ObservationCredentialStore.Encode(user, password);
        try
        {
            var parsed = ObservationCredentialStore.Parse(bytes);
            Assert.Equal(user, parsed.ClusterUser);
            Assert.Equal(password, parsed.ClusterPassword);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        AssertRedacted(() => ObservationCredentialStore.Encode("\ud800", Secret));
        AssertRedacted(() => ObservationCredentialStore.Encode(null!, Secret));
        AssertRedacted(() => ObservationCredentialStore.Encode("operator", null!));
    }

    [Fact]
    public void NonWindowsDoesNotTouchFilesystemOrExposeSecrets()
    {
        if (OperatingSystem.IsWindows()) return;
        AssertRedacted(() => ObservationCredentialStore.Load("untrusted-relative-path-" + Secret));
        AssertRedacted(() => ObservationCredentialStore.Import("untrusted-relative-path", "operator", Secret));
    }

    [Fact]
    public void WindowsCurrentUserRoundTripAndNoOverwrite()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "observation-credential-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ObservationCredentialStore.Import(directory, "operator", Secret);
            var original = File.ReadAllBytes(Path.Combine(directory, ObservationCredentialStore.FileName));
            var loaded = ObservationCredentialStore.Load(directory);
            Assert.Equal(Secret, loaded.ClusterPassword);
            AssertRedacted(() => ObservationCredentialStore.Import(directory, "other", "different-password"));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(directory, ObservationCredentialStore.FileName)));
            AssertRedacted(() => ObservationCredentialStore.Import("relative-dir", "operator", Secret));
            AssertRedacted(() => ObservationCredentialStore.Load("relative-dir"));
            var malformed = Utf8("{\"version\":2}");
            try
            {
                File.WriteAllBytes(Path.Combine(directory, ObservationCredentialStore.FileName),
                    ProtectedData.Protect(malformed, null, DataProtectionScope.CurrentUser));
                AssertRedacted(() => ObservationCredentialStore.Load(directory));
            }
            finally { CryptographicOperations.ZeroMemory(malformed); }
            File.WriteAllBytes(Path.Combine(directory, ObservationCredentialStore.FileName), new byte[17]);
            AssertRedacted(() => ObservationCredentialStore.Load(directory));
            File.WriteAllBytes(Path.Combine(directory, ObservationCredentialStore.FileName), new byte[ObservationCredentialStore.MaxCiphertextBytes + 1]);
            AssertRedacted(() => ObservationCredentialStore.Load(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void AssertRedacted(Action action)
    {
        var error = Assert.Throws<ObservationCredentialException>(action);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("untrusted-relative-path", error.ToString(), StringComparison.Ordinal);
    }
}
