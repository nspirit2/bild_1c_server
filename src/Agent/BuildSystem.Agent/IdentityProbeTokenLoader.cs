using System.Security.Cryptography;
using System.Text;

namespace BuildSystem.Agent;

internal sealed class IdentityProbeTokenUnavailableException : Exception
{
    public IdentityProbeTokenUnavailableException() : base("IdentityProbeTokenUnavailable") { }
    public override string ToString() => "IdentityProbeTokenUnavailable";
}

/// <summary>Probe-only bounded token loading. Does not attest ACLs, reparse targets or service identity.</summary>
internal static class IdentityProbeTokenLoader
{
    internal const int MaximumEncryptedBytes = 16384;

    internal static string Load(string path)
    {
        byte[]? encrypted = null;
        byte[]? plaintext = null;
        try
        {
            // No file open or content read on unsupported platforms.
            if (!OperatingSystem.IsWindows()) throw new IdentityProbeTokenUnavailableException();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                encrypted = ReadEncryptedToken(stream);
            plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            // Agent credentials are exactly 64 ASCII hex characters; no replacement decoding.
            if (plaintext.Length != 64 || plaintext.Any(b => !((b >= '0' && b <= '9') ||
                (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F'))))
                throw new IdentityProbeTokenUnavailableException();
            return Encoding.ASCII.GetString(plaintext);
        }
        catch (Exception) { throw new IdentityProbeTokenUnavailableException(); }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    /// <summary>Caller owns stream and returned encrypted bytes. Never uses Length or seeks.</summary>
    internal static byte[] ReadEncryptedToken(Stream stream)
    {
        var scratch = new byte[MaximumEncryptedBytes + 1];
        try
        {
            ArgumentNullException.ThrowIfNull(stream);
            var count = 0;
            while (count < scratch.Length)
            {
                var remaining = scratch.Length - count;
                var read = stream.Read(scratch, count, remaining);
                if (read < 0 || read > remaining) throw new IdentityProbeTokenUnavailableException();
                if (read == 0) break;
                count += read;
            }
            if (count is < 1 or > MaximumEncryptedBytes) throw new IdentityProbeTokenUnavailableException();
            return scratch.AsSpan(0, count).ToArray();
        }
        catch (Exception) { throw new IdentityProbeTokenUnavailableException(); }
        finally { CryptographicOperations.ZeroMemory(scratch); }
    }
}