using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Tablet.Persistence.Entities;

namespace Vendomat.Controller.Tablet.Services;

public sealed class SqliteMachineSettingsRepository(LocalDatabaseService database) : IMachineSettingsRepository
{
    public async Task<MachineSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        var entity = await database.Connection.Table<MachineSettingsEntity>().FirstOrDefaultAsync();
        if (entity is not null)
        {
            var settings = entity.ToDomain();
            var shouldPersistNormalizedAdminPasscode = !string.Equals(
                entity.AdminPasscodeHash,
                settings.AdminPasscodeHash,
                StringComparison.Ordinal);

            if (ShouldDisableLegacyEscrow(settings))
            {
                settings.BillValidatorEscrowMode = false;
                await SaveAsync(settings, cancellationToken);
            }
            else if (shouldPersistNormalizedAdminPasscode)
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

        await database.Connection.InsertOrReplaceAsync(MachineSettingsEntity.FromDomain(settings));
    }

    private static bool ShouldDisableLegacyEscrow(MachineSettings settings) =>
        settings.BillValidatorEscrowMode
        && string.Equals(settings.BillValidatorPortName, "/dev/ttyACM0", StringComparison.OrdinalIgnoreCase)
        && settings.BillValidatorBaudRate == 115200;
}
