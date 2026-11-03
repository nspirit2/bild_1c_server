using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Agent.Tools;

namespace BuildSystem.Agent;

public sealed class ObservationCredentialException : Exception
{
    public ObservationCredentialException() : base("Observation credentials unavailable.") { }
    public override string ToString() => "ObservationCredentialException: Observation credentials unavailable.";
}

/// <summary>
/// Separate CurrentUser DPAPI credentials, never the agent enrollment token. The operator must own
/// and protect the existing absolute directory and its ancestors: temporary files inherit its ACL.
/// No ACL ownership is inferred or repaired here. Managed credential strings cannot be zeroed.
/// </summary>
public static class ObservationCredentialStore
{
    public const string FileName = "observation-credentials.dpapi";
    public const int MaxCiphertextBytes = 16 * 1024;
    public const int MaxPlaintextBytes = 8 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static InfobaseInventoryObserverCredentials Load(string dataDirectory)
    {
        byte[]? ciphertext = null, plaintext = null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new ObservationCredentialException();
            var path = CredentialPath(dataDirectory);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length is <= 0 or > MaxCiphertextBytes) throw new ObservationCredentialException();
                ciphertext = new byte[checked((int)file.Length)];
                file.ReadExactly(ciphertext);
                if (file.ReadByte() != -1) throw new ObservationCredentialException();
            }
            plaintext = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
            return Parse(plaintext);
        }
        catch { throw new ObservationCredentialException(); }
        finally
        {
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>API-only provisioning; atomically publishes without replacing an existing credential.</summary>
    public static void Import(string directory, string user, string password)
    {
        byte[]? plaintext = null, ciphertext = null;
        string? temporary = null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new ObservationCredentialException();
            var path = CredentialPath(directory);
            plaintext = Encode(user, password);
            ciphertext = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            if (ciphertext.Length is <= 0 or > MaxCiphertextBytes) throw new ObservationCredentialException();
            temporary = Path.Combine(directory, ".observation-credentials-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(ciphertext);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        catch { throw new ObservationCredentialException(); }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (temporary is not null)
                try { File.Delete(temporary); } catch { /* Never expose cleanup paths or diagnostics. */ }
        }
    }

    /// <summary>Pure schema decoder; accepts only exact field names and strict UTF-8.</summary>
    public static InfobaseInventoryObserverCredentials Parse(ReadOnlySpan<byte> plaintext)
    {
        try
        {
            if (plaintext.Length is <= 0 or > MaxPlaintextBytes) throw new ObservationCredentialException();
            _ = StrictUtf8.GetCharCount(plaintext);
            var reader = new Utf8JsonReader(plaintext, new JsonReaderOptions { MaxDepth = 4 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new ObservationCredentialException();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? user = null, password = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new ObservationCredentialException();
                var name = reader.GetString()!;
                if (!seen.Add(name) || !reader.Read()) throw new ObservationCredentialException();
                switch (name)
                {
                    case "version":
                        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var version) || version != 1)
                            throw new ObservationCredentialException();
                        break;
                    case "clusterUser": user = reader.GetString(); break;
                    case "clusterPassword": password = reader.GetString(); break;
                    default: throw new ObservationCredentialException();
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || seen.Count != 3 || reader.Read())
                throw new ObservationCredentialException();
            Validate(user, password);
            return new InfobaseInventoryObserverCredentials(user!, password!);
        }
        catch { throw new ObservationCredentialException(); }
    }

    /// <summary>Pure encoder. The caller owns and should zero the returned plaintext bytes.</summary>
    public static byte[] Encode(string user, string password)
    {
        byte[]? bytes = null;
        try
        {
            Validate(user, password);
            bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, clusterUser = user, clusterPassword = password });
            if (bytes.Length > MaxPlaintextBytes) throw new ObservationCredentialException();
            return bytes;
        }
        catch
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            throw new ObservationCredentialException();
        }
    }

    private static void Validate(string? user, string? password)
    {
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || user.Length > 128 || password.Length > 256 ||
            user.Any(char.IsControl) || password.Any(char.IsControl)) throw new ObservationCredentialException();
        _ = StrictUtf8.GetByteCount(user);
        _ = StrictUtf8.GetByteCount(password);
    }

    private static string CredentialPath(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
            directory.Any(char.IsControl) || !Directory.Exists(directory) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new ObservationCredentialException();
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ObservationCredentialException();
        return path;
    }
}
