using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Tablet.Persistence.Entities;

namespace Vendomat.Controller.Tablet.Services;

public sealed class SqliteMachineSettingsRepository(
    LocalDatabaseService database,
    DeviceSecretStore deviceSecretStore) : IMachineSettingsRepository
{
    public async Task<MachineSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        var entity = await database.Connection.Table<MachineSettingsEntity>().FirstOrDefaultAsync();
        if (entity is not null)
        {
            var settings = entity.ToDomain();
            settings.CloudMachineToken = await deviceSecretStore.UnprotectAsync(settings.CloudMachineToken);
            settings.CompanionAccessToken = await deviceSecretStore.UnprotectAsync(settings.CompanionAccessToken);
            var shouldPersistNormalizedAdminPasscode = !string.Equals(
                entity.AdminPasscodeHash,
                settings.AdminPasscodeHash,
                StringComparison.Ordinal);
            var shouldPersistProtectedSecrets =
                !deviceSecretStore.IsProtected(entity.CloudMachineToken)
                || !deviceSecretStore.IsProtected(entity.CompanionAccessToken);

            if (ShouldDisableLegacyEscrow(settings))
            {
                settings.BillValidatorEscrowMode = false;
                await SaveAsync(settings, cancellationToken);
            }
            else if (shouldPersistNormalizedAdminPasscode || shouldPersistProtectedSecrets)
            {
                await SaveAsync(settings, cancellationToken);
            }

            return settings;
        }

        var defaults = new MachineSettings();
        await SaveAsync(defaults, cancellationToken);
        return defaults;
    }

    public async Task SaveAsync(MachineSettings settings, CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        if (ShouldDisableLegacyEscrow(settings))
        {
            settings.BillValidatorEscrowMode = false;
        }

        var entity = MachineSettingsEntity.FromDomain(settings);
        entity.CloudMachineToken = await deviceSecretStore.ProtectAsync(entity.CloudMachineToken);
        entity.CompanionAccessToken = await deviceSecretStore.ProtectAsync(entity.CompanionAccessToken);
        await database.Connection.InsertOrReplaceAsync(entity);
    }

    private static bool ShouldDisableLegacyEscrow(MachineSettings settings) =>
        settings.BillValidatorEscrowMode
        && string.Equals(settings.BillValidatorPortName, "/dev/ttyACM0", StringComparison.OrdinalIgnoreCase)
        && settings.BillValidatorBaudRate == 115200;
}
