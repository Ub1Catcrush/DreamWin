using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace DreamWin.Models;

public class ReceiverConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "My Receiver";
    public string Host { get; set; } = "192.168.1.100";
    public int Port { get; set; } = 80;
    public string? Username { get; set; }
    public bool UseHttps { get; set; } = false;
    public bool AcceptSelfSignedCert { get; set; } = false;
    public int StreamingPort { get; set; } = 8001;
    public bool IsDefault { get; set; } = false;

    // Persisted as DPAPI-encrypted base64; never store plaintext password in JSON
    public string? PasswordEncrypted { get; set; }

    // In-memory only — populated after DecryptPassword(), cleared before serialisation
    [JsonIgnore]
    public string? Password { get; set; }

    public string BaseUrl => $"{(UseHttps ? "https" : "http")}://{Host}:{Port}";
    // Streaming port always runs plain HTTP on Enigma2 regardless of web-UI HTTPS setting
    public string StreamBaseUrl => $"http://{Host}:{StreamingPort}";

    /// <summary>Encrypt the in-memory Password into PasswordEncrypted using DPAPI (CurrentUser scope).</summary>
    public void EncryptPassword()
    {
        if (string.IsNullOrEmpty(Password))
        {
            PasswordEncrypted = null;
            return;
        }
        try
        {
            var plain = Encoding.UTF8.GetBytes(Password);
            var cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            PasswordEncrypted = Convert.ToBase64String(cipher);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReceiverConfig] EncryptPassword failed: {ex.Message}");
            // Leave PasswordEncrypted null rather than store plaintext
            PasswordEncrypted = null;
        }
    }

    /// <summary>Decrypt PasswordEncrypted back into Password.</summary>
    public void DecryptPassword()
    {
        if (string.IsNullOrEmpty(PasswordEncrypted))
        {
            Password = null;
            return;
        }
        try
        {
            var cipher = Convert.FromBase64String(PasswordEncrypted);
            var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            Password = Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReceiverConfig] DecryptPassword failed: {ex.Message}");
            Password = null;
        }
    }
}
