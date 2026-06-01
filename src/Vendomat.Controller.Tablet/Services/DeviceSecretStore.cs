using System.Security.Cryptography;
using Microsoft.Maui.Storage;

namespace Vendomat.Controller.Tablet.Services;

public sealed class DeviceSecretStore
{
    private const string ProtectedPrefix = "enc-v1:";
    private const string KeyStorageName = "vendomat.tablet.device-secrets.aes-key";

    public bool IsProtected(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith(ProtectedPrefix, StringComparison.Ordinal);

    public async Task<string> ProtectAsync(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (IsProtected(normalized))
        {
            return normalized;
        }

        var key = await GetOrCreateKeyAsync();
        var plaintext = System.Text.Encoding.UTF8.GetBytes(normalized);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return $"{ProtectedPrefix}{Convert.ToBase64String(nonce)}:{Convert.ToBase64String(ciphertext)}:{Convert.ToBase64String(tag)}";
    }

    public async Task<string> UnprotectAsync(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) || !IsProtected(normalized))
        {
            return normalized;
        }

        var parts = normalized[ProtectedPrefix.Length..].Split(':');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException("Secret local invalid.");
        }

        var key = await GetOrCreateKeyAsync();
        var nonce = Convert.FromBase64String(parts[0]);
        var ciphertext = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return System.Text.Encoding.UTF8.GetString(plaintext);
    }

    private static async Task<byte[]> GetOrCreateKeyAsync()
    {
        var stored = await SecureStorage.Default.GetAsync(KeyStorageName);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return Convert.FromBase64String(stored);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        await SecureStorage.Default.SetAsync(KeyStorageName, Convert.ToBase64String(key));
        return key;
    }
}
