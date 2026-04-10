using System.Text.Json;
using SQLite;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Domain.Security;

namespace Vendomat.Controller.Tablet.Persistence.Entities;

[Table("machine_settings")]
public sealed class MachineSettingsEntity
{
    [PrimaryKey]
    public int Id { get; set; } = 1;

    public string MachineId { get; set; } = Guid.NewGuid().ToString("N");
    public string MachineName { get; set; } = string.Empty;
    public decimal PricePerLiter { get; set; }
    public int PulsesPerLiter { get; set; }
    public decimal CurrentStockLiters { get; set; }
    public decimal TankCapacityLiters { get; set; }
    public decimal LowStockThresholdLiters { get; set; }
    public bool CashPaymentEnabled { get; set; }
    public bool CardPaymentEnabled { get; set; }
    public bool BillValidatorEnabled { get; set; } = true;
    public string BillValidatorPortName { get; set; } = "/dev/ttyACM0";
    public int BillValidatorBaudRate { get; set; } = 115200;
    public bool BillValidatorEscrowMode { get; set; } = false;
    public bool Esp32Enabled { get; set; } = true;
    public string Esp32PortName { get; set; } = "/dev/ttyS3";
    public int Esp32BaudRate { get; set; } = 115200;
    public bool Esp32AutoDiscover { get; set; } = true;
    public string ContactPhone { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string LocalApiBaseUrl { get; set; } = string.Empty;
    public string PublicApiBaseUrl { get; set; } = string.Empty;
    public string CloudApiBaseUrl { get; set; } = string.Empty;
    public string CloudMachineToken { get; set; } = string.Empty;
    public string CompanionAccessToken { get; set; } = string.Empty;
    public string AdminPasscodeHash { get; set; } = AdminPasscodeHasher.DefaultHash;
    public long PromoRotationTicks { get; set; }
    public string CashChannelsJson { get; set; } = "[]";

    public MachineSettings ToDomain()
    {
        var hasLegacyValidatorDefaults = string.IsNullOrWhiteSpace(BillValidatorPortName) && BillValidatorBaudRate <= 0;
        var hasLegacyEsp32Defaults = string.IsNullOrWhiteSpace(Esp32PortName) && Esp32BaudRate <= 0;

        return new MachineSettings
        {
            MachineId = Guid.Parse(MachineId),
            MachineName = MachineName,
            PricePerLiter = PricePerLiter,
            PulsesPerLiter = PulsesPerLiter,
            CurrentStockLiters = CurrentStockLiters,
            TankCapacityLiters = TankCapacityLiters,
            LowStockThresholdLiters = LowStockThresholdLiters,
            CashPaymentEnabled = CashPaymentEnabled,
            CardPaymentEnabled = CardPaymentEnabled,
            BillValidatorEnabled = hasLegacyValidatorDefaults ? true : BillValidatorEnabled,
            BillValidatorPortName = string.IsNullOrWhiteSpace(BillValidatorPortName) ? "/dev/ttyACM0" : BillValidatorPortName,
            BillValidatorBaudRate = BillValidatorBaudRate <= 0 ? 115200 : BillValidatorBaudRate,
            BillValidatorEscrowMode = hasLegacyValidatorDefaults ? false : BillValidatorEscrowMode,
            Esp32Enabled = hasLegacyEsp32Defaults ? true : Esp32Enabled,
            Esp32PortName = string.IsNullOrWhiteSpace(Esp32PortName) ? "/dev/ttyS3" : Esp32PortName,
            Esp32BaudRate = Esp32BaudRate <= 0 ? 115200 : Esp32BaudRate,
            Esp32AutoDiscover = hasLegacyEsp32Defaults || Esp32AutoDiscover,
            ContactPhone = ContactPhone,
            ContactEmail = ContactEmail,
            LocalApiBaseUrl = LocalApiBaseUrl,
            PublicApiBaseUrl = PublicApiBaseUrl,
            CloudApiBaseUrl = CloudApiBaseUrl,
            CloudMachineToken = CloudMachineToken,
            CompanionAccessToken = CompanionAccessToken,
            AdminPasscodeHash = AdminPasscodeHasher.NormalizeStoredHash(AdminPasscodeHash),
            PromoRotationInterval = TimeSpan.FromTicks(PromoRotationTicks),
            CashChannels = JsonSerializer.Deserialize<List<CashChannelSetting>>(CashChannelsJson) ?? [],
        };
    }

    public static MachineSettingsEntity FromDomain(MachineSettings settings) => new()
    {
        Id = 1,
        MachineId = settings.MachineId.ToString("N"),
        MachineName = settings.MachineName,
        PricePerLiter = settings.PricePerLiter,
        PulsesPerLiter = settings.PulsesPerLiter,
        CurrentStockLiters = settings.CurrentStockLiters,
        TankCapacityLiters = settings.TankCapacityLiters,
        LowStockThresholdLiters = settings.LowStockThresholdLiters,
        CashPaymentEnabled = settings.CashPaymentEnabled,
        CardPaymentEnabled = settings.CardPaymentEnabled,
        BillValidatorEnabled = settings.BillValidatorEnabled,
        BillValidatorPortName = settings.BillValidatorPortName,
        BillValidatorBaudRate = settings.BillValidatorBaudRate,
        BillValidatorEscrowMode = settings.BillValidatorEscrowMode,
        Esp32Enabled = settings.Esp32Enabled,
        Esp32PortName = settings.Esp32PortName,
        Esp32BaudRate = settings.Esp32BaudRate,
        Esp32AutoDiscover = settings.Esp32AutoDiscover,
        ContactPhone = settings.ContactPhone,
        ContactEmail = settings.ContactEmail,
        LocalApiBaseUrl = settings.LocalApiBaseUrl,
        PublicApiBaseUrl = settings.PublicApiBaseUrl,
        CloudApiBaseUrl = settings.CloudApiBaseUrl,
        CloudMachineToken = settings.CloudMachineToken,
        CompanionAccessToken = settings.CompanionAccessToken,
        AdminPasscodeHash = AdminPasscodeHasher.NormalizeStoredHash(settings.AdminPasscodeHash),
        PromoRotationTicks = settings.PromoRotationInterval.Ticks,
        CashChannelsJson = JsonSerializer.Serialize(settings.CashChannels),
    };
}
