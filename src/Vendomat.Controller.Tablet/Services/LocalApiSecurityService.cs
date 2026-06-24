using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Android.Util;

namespace Vendomat.Controller.Tablet.Services;

public sealed class LocalApiSecurityService(DeviceSecretStore deviceSecretStore)
{
    private const string StartupTag = "VendomatStartup";
    private const int HttpPort = 1326;
    private const int HttpsPort = 1327;
    private readonly SemaphoreSlim _certificateLock = new(1, 1);
    private X509Certificate2? _cachedCertificate;

    private string CertificatePath => Path.Combine(FileSystem.AppDataDirectory, "vendomat-local-api.pfx");

    public async Task<X509Certificate2> GetServerCertificateAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedCertificate is not null)
        {
            return _cachedCertificate;
        }

        await _certificateLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedCertificate is not null)
            {
                return _cachedCertificate;
            }

            var certificatePassword = await deviceSecretStore.GetOrCreateLocalApiCertificatePasswordAsync();

            if (File.Exists(CertificatePath))
            {
                _cachedCertificate = LoadExistingCertificate(
                    await File.ReadAllBytesAsync(CertificatePath, cancellationToken),
                    certificatePassword);
                return _cachedCertificate;
            }

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=Vendomat Controller Local API",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
            foreach (var address in LanAddressResolver.GetLocalIpAddresses())
            {
                sanBuilder.AddIpAddress(address);
            }

            request.CertificateExtensions.Add(sanBuilder.Build());

            using var rawCertificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(5));

            Directory.CreateDirectory(Path.GetDirectoryName(CertificatePath)!);
            var exportBytes = rawCertificate.Export(X509ContentType.Pfx, certificatePassword);
            await File.WriteAllBytesAsync(CertificatePath, exportBytes, cancellationToken);

            _cachedCertificate = new X509Certificate2(
                exportBytes,
                certificatePassword,
                X509KeyStorageFlags.Exportable);
            Log.Info(StartupTag, $"Generated local API certificate {_cachedCertificate.Thumbprint}");
            return _cachedCertificate;
        }
        finally
        {
            _certificateLock.Release();
        }
    }

    private X509Certificate2 LoadExistingCertificate(byte[] pfxBytes, string certificatePassword)
    {
        try
        {
            return new X509Certificate2(pfxBytes, certificatePassword, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException)
        {
            // Migrate a legacy PFX that was stored without a password, preserving the same
            // certificate/key (and therefore the pinned thumbprint) under the new password.
            var legacyCertificate = new X509Certificate2(pfxBytes, string.Empty, X509KeyStorageFlags.Exportable);
            try
            {
                var reExported = legacyCertificate.Export(X509ContentType.Pfx, certificatePassword);
                File.WriteAllBytes(CertificatePath, reExported);
                Log.Info(StartupTag, $"Re-protected local API certificate {legacyCertificate.Thumbprint}");
                return new X509Certificate2(reExported, certificatePassword, X509KeyStorageFlags.Exportable);
            }
            finally
            {
                legacyCertificate.Dispose();
            }
        }
    }

    public async Task<string> GetCertificateFingerprintAsync(CancellationToken cancellationToken = default)
    {
        var certificate = await GetServerCertificateAsync(cancellationToken);
        return NormalizeThumbprint(certificate.Thumbprint);
    }

    public string BuildHttpsBaseUrl(string? localApiBaseUrl)
    {
        if (!LanAddressResolver.TryCreateBaseUri(localApiBaseUrl, out var baseUri))
        {
            return string.Empty;
        }

        var builder = new UriBuilder(baseUri)
        {
            Scheme = Uri.UriSchemeHttps,
            Host = LanAddressResolver.ResolveHost(baseUri.Host),
            Port = HttpsPort,
        };

        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    public async Task OpenFirewallPortsAsync(CancellationToken cancellationToken = default)
    {
        await ExecutePrivilegedCommandAsync($"iptables -C INPUT -p tcp --dport {HttpPort} -j ACCEPT || iptables -I INPUT 1 -p tcp --dport {HttpPort} -j ACCEPT", cancellationToken);
        await ExecutePrivilegedCommandAsync($"iptables -C INPUT -p tcp --dport {HttpsPort} -j ACCEPT || iptables -I INPUT 1 -p tcp --dport {HttpsPort} -j ACCEPT", cancellationToken);
        await ExecutePrivilegedCommandAsync($"ip6tables -C INPUT -p tcp --dport {HttpPort} -j ACCEPT || ip6tables -I INPUT 1 -p tcp --dport {HttpPort} -j ACCEPT", cancellationToken);
        await ExecutePrivilegedCommandAsync($"ip6tables -C INPUT -p tcp --dport {HttpsPort} -j ACCEPT || ip6tables -I INPUT 1 -p tcp --dport {HttpsPort} -j ACCEPT", cancellationToken);
    }

    private static string NormalizeThumbprint(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();

    private static async Task ExecutePrivilegedCommandAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Java.Lang.Runtime.GetRuntime().Exec(["su", "0", "sh", "-c", command]);
            await Task.Run(() => process.WaitFor(), cancellationToken);
            if (process.ExitValue() != 0)
            {
                Log.Warn(StartupTag, $"Privilege command failed: {command}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn(StartupTag, $"Privilege command error: {command} => {ex.Message}");
        }
    }
}
