using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Domain.Security;
using Xunit;

namespace Vendomat.Controller.Domain.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void AdminPasscodeHasherDetectsDefaultHash()
    {
        Assert.True(AdminPasscodeHasher.IsDefaultHash(AdminPasscodeHasher.DefaultHash));
        Assert.False(AdminPasscodeHasher.IsDefaultHash(AdminPasscodeHasher.Hash("9876")));
    }

    [Fact]
    public void TokenHashForStorageDoesNotExposeReusableToken()
    {
        const string token = "ABCDEF0123456789ABCDEF0123456789";

        var stored = CompanionAccessTokenSecurity.HashForStorage(token);

        Assert.NotEqual(token, stored);
        Assert.DoesNotContain(token, stored, StringComparison.Ordinal);
        Assert.True(CompanionAccessTokenSecurity.IsStoredHash(stored));
        Assert.True(CompanionAccessTokenSecurity.Verify(stored, token));
        Assert.False(CompanionAccessTokenSecurity.Verify(stored, "wrong-token"));
    }

    [Fact]
    public void SnapshotSanitizerRemovesSecretsWithoutMutatingSource()
    {
        var snapshot = new MachineStatusSnapshot
        {
            Settings = new MachineSettings
            {
                CloudMachineToken = "machine-token",
                CompanionAccessToken = "companion-token",
                AdminPasscodeHash = AdminPasscodeHasher.Hash("9876"),
            },
        };

        var sanitized = MachineSnapshotSanitizer.ForExternalApi(snapshot);

        Assert.Empty(sanitized.Settings.CloudMachineToken);
        Assert.Empty(sanitized.Settings.CompanionAccessToken);
        Assert.Empty(sanitized.Settings.AdminPasscodeHash);
        Assert.Equal("machine-token", snapshot.Settings.CloudMachineToken);
        Assert.Equal("companion-token", snapshot.Settings.CompanionAccessToken);
        Assert.NotEmpty(snapshot.Settings.AdminPasscodeHash);
    }
}
