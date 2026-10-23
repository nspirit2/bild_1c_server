using System.Security.Cryptography;
using System.Text;

namespace BuildSystem.Agent;

public static class TokenStore
{
    public static void Import(string path, string token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI credentials require Windows.");
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid credential format.");
        var bytes = Encoding.UTF8.GetBytes(token);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            // Flush a sibling temporary file, then publish atomically without overwriting a credential.
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(encrypted);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static string Load(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI credentials require Windows.");
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
// end
