using System.Security.Cryptography;
using System.Text;

namespace Vendomat.Controller.Domain.Security;

/// <summary>
/// Hashes the admin passcode with PBKDF2-SHA256 (salted, iterated) so a leaked
/// settings store cannot be brute-forced instantly. Legacy <c>sha256:</c> hashes
/// produced by older builds are still verifiable for backward compatibility.
/// </summary>
public static class AdminPasscodeHasher
{
    public const string DefaultPasscode = "1234";

    private const string Pbkdf2Prefix = "pbkdf2:";
    private const string LegacyPrefix = "sha256:";
    private const string LegacyPepper = "Vendomat.Controller.Admin";

    private const int SaltLength = 16;
    private const int HashLength = 32;
    private const int Iterations = 120_000;

    // The default passcode is a public constant, so salting it adds no security.
    // Cache a single computed hash so property initializers / clones stay cheap.
    private static readonly string CachedDefaultHash = Hash(DefaultPasscode);

    public static string DefaultHash => CachedDefaultHash;

    public static string Hash(string passcode)
    {
        var normalized = NormalizePasscode(passcode);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(normalized),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashLength);

        return $"{Pbkdf2Prefix}{Iterations}:{Convert.ToHexString(salt)}:{Convert.ToHexString(hash)}";
    }

    public static bool Verify(string? storedHash, string? passcode)
    {
        if (string.IsNullOrWhiteSpace(passcode))
        {
            return false;
        }

        var normalizedPasscode = NormalizePasscode(passcode);
        var normalizedStoredHash = (storedHash ?? string.Empty).Trim();

        if (normalizedStoredHash.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal))
        {
            return VerifyPbkdf2(normalizedStoredHash, normalizedPasscode);
        }

        // Legacy single-pass SHA256 hashes remain valid until the passcode is rotated.
        var legacyStoredHash = normalizedStoredHash.StartsWith(LegacyPrefix, StringComparison.Ordinal)
            ? normalizedStoredHash
            : LegacySha256Hash(normalizedStoredHash);

        return FixedTimeStringEquals(legacyStoredHash, LegacySha256Hash(normalizedPasscode));
    }

    public static bool IsDefaultHash(string? storedHash) => Verify(storedHash, DefaultPasscode);

    public static string NormalizeStoredHash(string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            return DefaultHash;
        }

        var normalized = storedHash.Trim();
        if (normalized.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal)
            || normalized.StartsWith(LegacyPrefix, StringComparison.Ordinal))
        {
            return normalized;
        }

        // A bare value is treated as a plaintext passcode and upgraded to a real hash.
        return Hash(normalized);
    }

    private static bool VerifyPbkdf2(string storedHash, string normalizedPasscode)
    {
        var parts = storedHash[Pbkdf2Prefix.Length..].Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var iterations)
            || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromHexString(parts[1]);
            expectedHash = Convert.FromHexString(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var providedHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(normalizedPasscode),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
    }

    private static string LegacySha256Hash(string passcode)
    {
        var payload = Encoding.UTF8.GetBytes($"{LegacyPepper}:{passcode}");
        var hash = SHA256.HashData(payload);
        return $"{LegacyPrefix}{Convert.ToHexString(hash)}";
    }

    private static bool FixedTimeStringEquals(string expected, string provided)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return expectedBytes.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private static string NormalizePasscode(string passcode) => passcode.Trim();
}
