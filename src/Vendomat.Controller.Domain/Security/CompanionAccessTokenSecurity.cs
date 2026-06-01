using System.Security.Cryptography;
using System.Text;

namespace Vendomat.Controller.Domain.Security;

public static class CompanionAccessTokenSecurity
{
    private const string StoredHashPrefix = "token-v1:";
    private const int SaltLength = 16;
    private const int AuditPrefixLength = 8;

    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToHexString(bytes);
    }

    public static bool Verify(string? expectedToken, string? providedToken)
    {
        var normalizedExpected = Normalize(expectedToken);
        var normalizedProvided = Normalize(providedToken);

        if (string.IsNullOrWhiteSpace(normalizedExpected) || string.IsNullOrWhiteSpace(normalizedProvided))
        {
            return false;
        }

        if (TryParseStoredHash(normalizedExpected, out _, out var salt, out var expectedHash))
        {
            var providedHash = Hash(normalizedProvided, salt);
            return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
        }

        return FixedTimeEquals(normalizedExpected, normalizedProvided);
    }

    public static string Normalize(string? token) => token?.Trim() ?? string.Empty;

    public static string HashForStorage(string? token)
    {
        var normalized = Normalize(token);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (IsStoredHash(normalized))
        {
            return normalized;
        }

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Hash(normalized, salt);
        return $"{StoredHashPrefix}{GetAuditPrefix(normalized)}:{Convert.ToHexString(salt)}:{Convert.ToHexString(hash)}";
    }

    public static bool IsStoredHash(string? value) =>
        Normalize(value).StartsWith(StoredHashPrefix, StringComparison.Ordinal);

    public static string GetAuditPrefix(string? tokenOrStoredHash)
    {
        var normalized = Normalize(tokenOrStoredHash);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (TryParseStoredHash(normalized, out var storedPrefix, out _, out _))
        {
            return storedPrefix;
        }

        return normalized.Length <= AuditPrefixLength
            ? normalized
            : normalized[..AuditPrefixLength];
    }

    private static byte[] Hash(string token, byte[] salt)
    {
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var payload = new byte[salt.Length + tokenBytes.Length];
        Buffer.BlockCopy(salt, 0, payload, 0, salt.Length);
        Buffer.BlockCopy(tokenBytes, 0, payload, salt.Length, tokenBytes.Length);
        return SHA256.HashData(payload);
    }

    private static bool TryParseStoredHash(string value, out string auditPrefix, out byte[] salt, out byte[] hash)
    {
        auditPrefix = string.Empty;
        salt = [];
        hash = [];

        if (!value.StartsWith(StoredHashPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = value[StoredHashPrefix.Length..].Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        try
        {
            auditPrefix = parts[0];
            salt = Convert.FromHexString(parts[1]);
            hash = Convert.FromHexString(parts[2]);
            return salt.Length == SaltLength && hash.Length == SHA256.HashSizeInBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool FixedTimeEquals(string expected, string provided)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return expectedBytes.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}
