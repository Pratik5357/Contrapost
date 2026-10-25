using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Contrapost.Application.Clients;

/// <summary>
/// API keys look like <c>cp_live_k3q9x7ab.&lt;secret&gt;</c> (design doc §11). The part before the dot
/// is the public prefix, stored as-is and used for lookup; only the SHA-256 hash of the secret is stored.
/// </summary>
public static class ApiKeys
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789"; // no 0/o/1/l lookalikes

    public sealed record Generated(string Prefix, string Secret, byte[] SecretHash)
    {
        public string Key => $"{Prefix}.{Secret}";
    }

    public static Generated Generate(string environment)
    {
        if (string.IsNullOrEmpty(environment) || !environment.All(char.IsAsciiLetterLower))
        {
            throw new ArgumentException("Environment must be lowercase ASCII letters, e.g. live, test, dev.", nameof(environment));
        }

        var prefix = $"cp_{environment}_{RandomNumberGenerator.GetString(Alphabet, 8)}";
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return new Generated(prefix, secret, Hash(secret));
    }

    /// <summary>Splits a presented key into prefix and secret; false if it isn't shaped like a key.</summary>
    public static bool TryParse(string? key, out string prefix, out string secret)
    {
        prefix = secret = "";
        var dot = key?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        if (key is null || dot <= 0 || dot == key.Length - 1 || !key.StartsWith("cp_", StringComparison.Ordinal))
        {
            return false;
        }

        prefix = key[..dot];
        secret = key[(dot + 1)..];
        return true;
    }

    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    /// <summary>Constant-time comparison, so response timing doesn't reveal how much of a secret matched.</summary>
    public static bool Matches(string secret, byte[] storedHash) =>
        CryptographicOperations.FixedTimeEquals(Hash(secret), storedHash);
}
