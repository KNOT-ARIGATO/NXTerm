using System.Security.Cryptography;
using System.Text;

namespace NXTerm;

/// <summary>Encrypts small secrets with Windows DPAPI so only the current Windows user on this PC can read them.</summary>
public static class Secrets
{
    public static string Protect(string text, byte[] entropy) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), entropy, DataProtectionScope.CurrentUser));

    /// <summary>Returns null when the value cannot be decrypted (another user, another PC, or damaged data).</summary>
    public static string? Unprotect(string base64, byte[] entropy)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(base64), entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }
}
