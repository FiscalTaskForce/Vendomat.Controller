using System.Security.Cryptography;
using System.Text;

namespace Vendomat.Controller.Domain.Security;

public static class AdminPasscodeHasher
{
    public const string DefaultPasscode = "1234";
    private const string Pepper = "Vendomat.Controller.Admin";
    private const string Prefix = "sha256:";

    public static string DefaultHash => Hash(DefaultPasscode);

    public static string Hash(string passcode)
    {
        var normalized = NormalizePasscode(passcode);
        var payload = Encoding.UTF8.GetBytes($"{Pepper}:{normalized}");
        var hash = SHA256.HashData(payload);
        return $"{Prefix}{Convert.ToHexString(hash)}";
    }

    public static bool Verify(string? storedHash, string? passcode)
    {
        if (string.IsNullOrWhiteSpace(passcode))
        {
            return false;
        }

        var normalizedStoredHash = NormalizeStoredHash(storedHash);
        return string.Equals(normalizedStoredHash, Hash(passcode), StringComparison.Ordinal);
    }

    public static bool IsDefaultHash(string? storedHash) =>
        string.Equals(NormalizeStoredHash(storedHash), DefaultHash, StringComparison.Ordinal);

    public static string NormalizeStoredHash(string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            return DefaultHash;
        }

        var normalized = storedHash.Trim();
        return normalized.StartsWith(Prefix, StringComparison.Ordinal)
            ? normalized
            : Hash(normalized);
    }

    private static string NormalizePasscode(string passcode) => passcode.Trim();
}
