using System.Security.Cryptography;
using System.Text;
using FlowCapture.Application;
using FlowCapture.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowCapture.Infrastructure.Security;

/// <summary>
/// AES-256-GCM with a key derived from the server's signing key. Good enough to keep an AI key unreadable in a database backup.
/// Changing Security:JwtSigningKey makes saved secrets unreadable, and the administrator simply enters the key again.
/// </summary>
public sealed class SecretProtector(IOptions<SecurityOptions> options) : ISecretProtector
{
    private readonly byte[] _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(options.Value.JwtSigningKey), 32,
        salt: Encoding.UTF8.GetBytes("flowcapture"), info: Encoding.UTF8.GetBytes("saved-secrets"));

    public string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[data.Length];
        using (var aes = new AesGcm(_key, 16)) aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string? Unprotect(string protectedValue)
    {
        try
        {
            var all = Convert.FromBase64String(protectedValue);
            if (all.Length < 28) return null;
            var nonce = all[..12];
            var tag = all[12..28];
            var cipher = all[28..];
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(_key, 16)) aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return null; }
    }
}
