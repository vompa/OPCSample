using System.Security.Cryptography;
using System.Text;

namespace OPCClient.Security;

/// <summary>
/// API-Keys sind zufällige Tokens mit hoher Entropie, daher genügt ein schneller Hash (SHA-256).
/// Für menschliche Passwörter wäre das falsch – dort braucht es ein KDF wie Argon2 oder PBKDF2.
/// </summary>
public static class ApiKeyHasher
{
    public static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public static bool TryParseHash(string? hex, out byte[] hash)
    {
        hash = [];
        if (hex is not { Length: 64 })
            return false;
        try
        {
            hash = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
