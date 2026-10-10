using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NXTerm.Connections;

/// <summary>A named connection the user saved, optionally with an encrypted password.</summary>
public sealed class SavedSession
{
    // Keeps the value from when the program was called NTerm, so passwords saved back then still decrypt.
    static readonly byte[] Entropy = "NTerm.SavedSession.v1"u8.ToArray();
    static readonly JsonSerializerOptions CompareJson = new() { IncludeFields = true };

    public string Name = "";
    public ConnectionParams Params = new();
    /// <summary>Password encrypted with Windows DPAPI for the current user (base64), or "".</summary>
    public string ProtectedPassword = "";
    public DateTime LastUsed;

    [JsonIgnore] public bool HasPassword => ProtectedPassword.Length > 0;

    public void SetPassword(string? password) =>
        ProtectedPassword = string.IsNullOrEmpty(password)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>The saved password, or "" when none is saved or it cannot be decrypted (e.g. another Windows user).</summary>
    public string GetPassword()
    {
        if (!HasPassword) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return ""; }
    }

    /// <summary>Connection parameters including the decrypted password.</summary>
    public ConnectionParams ToParams()
    {
        var p = Params.Clone();
        p.Password = GetPassword();
        return p;
    }

    /// <summary>True when both describe the same connection settings (passwords are not compared).</summary>
    public static bool SameSettings(ConnectionParams a, ConnectionParams b) =>
        JsonSerializer.Serialize(a, CompareJson) == JsonSerializer.Serialize(b, CompareJson);

    public static string SuggestName(ConnectionParams p) => p.Type switch
    {
        ConnectionType.Serial => $"{p.PortName} {p.Baud}",
        ConnectionType.Ssh when p.User.Length > 0 => $"{p.User}@{p.Host}",
        _ => p.Host,
    };

    public string Summary => Params.Type switch
    {
        ConnectionType.Serial => $"Serial  {Params.PortName} {Params.Baud}",
        ConnectionType.Ssh => $"SSH  {(Params.User.Length > 0 ? Params.User + "@" : "")}{Params.Host}" + (Params.Port != 22 ? $":{Params.Port}" : ""),
        ConnectionType.Telnet => $"Telnet  {Params.Host}" + (Params.Port != 23 ? $":{Params.Port}" : ""),
        _ => $"TCP  {Params.Host}:{Params.Port}",
    };
}
