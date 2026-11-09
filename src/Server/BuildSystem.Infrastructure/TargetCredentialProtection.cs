using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace BuildSystem.Infrastructure;

/// <summary>Secret values are deliberately excluded from diagnostic formatting.</summary>
public sealed record TargetCredentialSecrets(
    string ClusterUser, string ClusterPassword, string SqlUser, string SqlPassword)
{
    public override string ToString() => "TargetCredentialSecrets [REDACTED]";
}

/// <summary>All four identifiers bind an envelope to its intended credential and endpoints.</summary>
public sealed record TargetCredentialScope(
    Guid CredentialId, Guid CanonicalClusterId, Guid CanonicalSqlServerId, Guid EndpointMappingId)
{
    public override string ToString() => "TargetCredentialScope [REDACTED]";
}

public interface ITargetCredentialProtector
{
    string Protect(TargetCredentialScope scope, TargetCredentialSecrets secrets);
    TargetCredentialSecrets Unprotect(TargetCredentialScope scope, string ciphertext);
}

/// <summary>A deliberately uniform failure: neither provider details nor input values are exposed.</summary>
public sealed class TargetCredentialProtectionException() : Exception("Target credentials unavailable.")
{
    public override string ToString() => "Target credentials unavailable.";
}

/// <summary>
/// Pure server-side envelope codec. Key storage and SQL persistence belong to the caller;
/// the provider must use the server's existing Data Protection key configuration, outside SQL.
/// Owned temporary byte buffers are cleared on a best-effort basis; library-internal scratch
/// is outside that guarantee. Managed input, output and parsed strings are immutable and cannot
/// be reliably erased, so callers must not log or retain them unnecessarily.
/// </summary>
public sealed class DataProtectionTargetCredentialProtector : ITargetCredentialProtector
{
    public const int MaxPlaintextBytes = 128 * 1024;
    public const int MaxCiphertextCharacters = 256 * 1024;
    private const int MaxFieldCharacters = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IDataProtectionProvider provider;

    public DataProtectionTargetCredentialProtector(IDataProtectionProvider provider)
    {
        this.provider = provider ?? throw new TargetCredentialProtectionException();
    }

    public string Protect(TargetCredentialScope scope, TargetCredentialSecrets secrets)
    {
        byte[]? plaintext = null;
        byte[]? protectedBytes = null;
        try
        {
            ValidateScope(scope);
            ValidateSecrets(secrets);
            plaintext = Serialize(secrets);
            if (plaintext.Length is < 1 or > MaxPlaintextBytes) throw Unavailable();
            protectedBytes = CreateProtector(scope).Protect(plaintext);
            if (protectedBytes is null || protectedBytes.Length == 0 ||
                ((long)protectedBytes.Length + 2) / 3 * 4 > MaxCiphertextCharacters)
                throw Unavailable();
            return Convert.ToBase64String(protectedBytes);
        }
        catch (Exception)
        {
            throw Unavailable();
        }
        finally
        {
            Clear(plaintext);
            Clear(protectedBytes);
        }
    }

    public TargetCredentialSecrets Unprotect(TargetCredentialScope scope, string ciphertext)
    {
        byte[]? protectedBytes = null;
        byte[]? plaintext = null;
        try
        {
            ValidateScope(scope);
            if (string.IsNullOrEmpty(ciphertext) || ciphertext.Length > MaxCiphertextCharacters)
                throw Unavailable();
            protectedBytes = Convert.FromBase64String(ciphertext);
            if (protectedBytes.Length == 0) throw Unavailable();
            plaintext = CreateProtector(scope).Unprotect(protectedBytes);
            if (plaintext is null || plaintext.Length is < 1 or > MaxPlaintextBytes)
                throw Unavailable();
            // Reject malformed UTF-8 rather than accepting replacement characters.
            _ = StrictUtf8.GetCharCount(plaintext);
            return Parse(plaintext);
        }
        catch (Exception)
        {
            throw Unavailable();
        }
        finally
        {
            Clear(plaintext);
            Clear(protectedBytes);
        }
    }

    private IDataProtector CreateProtector(TargetCredentialScope scope) =>
        provider.CreateProtector("BuildSystem.TargetCreationCredentials", "v1",
            scope.CredentialId.ToString("D"), scope.CanonicalClusterId.ToString("D"),
            scope.CanonicalSqlServerId.ToString("D"), scope.EndpointMappingId.ToString("D"));

    private static void ValidateScope(TargetCredentialScope scope)
    {
        if (scope is null || scope.CredentialId == Guid.Empty || scope.CanonicalClusterId == Guid.Empty ||
            scope.CanonicalSqlServerId == Guid.Empty || scope.EndpointMappingId == Guid.Empty)
            throw Unavailable();
    }

    private static void ValidateSecrets(TargetCredentialSecrets secrets)
    {
        if (secrets is null) throw Unavailable();
        ValidateField(secrets.ClusterUser);
        ValidateField(secrets.ClusterPassword);
        ValidateField(secrets.SqlUser);
        ValidateField(secrets.SqlPassword);
    }

    private static void ValidateField(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxFieldCharacters) throw Unavailable();
        foreach (var character in value)
            if (char.IsControl(character)) throw Unavailable();
        // Also disallow unpaired UTF-16 surrogates, which JSON writers otherwise replace.
        _ = StrictUtf8.GetByteCount(value);
    }

    private static byte[] Serialize(TargetCredentialSecrets secrets)
    {
        // Supply owned, fixed storage directly: no stream staging or growing pooled buffers.
        var buffer = new FixedPlaintextBuffer();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("clusterUser", secrets.ClusterUser);
                writer.WriteString("clusterPassword", secrets.ClusterPassword);
                writer.WriteString("sqlUser", secrets.SqlUser);
                writer.WriteString("sqlPassword", secrets.SqlPassword);
                writer.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            buffer.Clear();
        }
    }

    private sealed class FixedPlaintextBuffer : IBufferWriter<byte>
    {
        private readonly byte[] bytes = new byte[MaxPlaintextBytes];
        private int written;

        public ReadOnlySpan<byte> WrittenSpan => bytes.AsSpan(0, written);

        public void Advance(int count)
        {
            if (count < 0 || count > bytes.Length - written) throw Unavailable();
            written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            ValidateSizeHint(sizeHint);
            return bytes.AsMemory(written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            ValidateSizeHint(sizeHint);
            return bytes.AsSpan(written);
        }

        private void ValidateSizeHint(int sizeHint)
        {
            if (sizeHint < 0 || Math.Max(sizeHint, 1) > bytes.Length - written)
                throw Unavailable();
        }

        public void Clear() => CryptographicOperations.ZeroMemory(bytes);
    }

    private static TargetCredentialSecrets Parse(ReadOnlySpan<byte> plaintext)
    {
        var reader = new Utf8JsonReader(plaintext, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 2
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw Unavailable();
        string? clusterUser = null, clusterPassword = null, sqlUser = null, sqlPassword = null;
        var seen = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw Unavailable();
            var field = reader.ValueTextEquals("clusterUser") ? 1 :
                reader.ValueTextEquals("clusterPassword") ? 2 :
                reader.ValueTextEquals("sqlUser") ? 4 :
                reader.ValueTextEquals("sqlPassword") ? 8 : 0;
            if (field == 0 || (seen & field) != 0) throw Unavailable();
            seen |= field;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String) throw Unavailable();
            var value = reader.GetString();
            if (value is null) throw Unavailable();
            ValidateField(value);
            switch (field)
            {
                case 1: clusterUser = value; break;
                case 2: clusterPassword = value; break;
                case 4: sqlUser = value; break;
                case 8: sqlPassword = value; break;
            }
        }
        if (reader.TokenType != JsonTokenType.EndObject || seen != 15 || reader.Read())
            throw Unavailable();
        return new TargetCredentialSecrets(clusterUser!, clusterPassword!, sqlUser!, sqlPassword!);
    }

    private static TargetCredentialProtectionException Unavailable() => new();

    private static void Clear(byte[]? buffer)
    {
        if (buffer is not null) CryptographicOperations.ZeroMemory(buffer);
    }
}
