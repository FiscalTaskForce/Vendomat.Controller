using System.Security.Cryptography;
using System.Text;

namespace Vendomat.Controller.Domain.Security;

public static class CompanionAccessTokenSecurity
{
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

        var expectedBytes = Encoding.UTF8.GetBytes(normalizedExpected);
        var providedBytes = Encoding.UTF8.GetBytes(normalizedProvided);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    public static string Normalize(string? token) => token?.Trim() ?? string.Empty;
}
