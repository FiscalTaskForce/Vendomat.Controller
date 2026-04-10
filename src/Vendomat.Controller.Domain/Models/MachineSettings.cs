using Vendomat.Controller.Domain.Security;

namespace Vendomat.Controller.Domain.Models;

public sealed class MachineSettings
{
    public Guid MachineId { get; set; } = Guid.NewGuid();
    public string MachineName { get; set; } = "Vendomat lapte";
    public decimal PricePerLiter { get; set; } = 10m;
    public int PulsesPerLiter { get; set; } = 450;
    public decimal CurrentStockLiters { get; set; } = 120m;
    public decimal TankCapacityLiters { get; set; } = 200m;
    public decimal LowStockThresholdLiters { get; set; } = 20m;
    public bool CashPaymentEnabled { get; set; } = true;
    public bool CardPaymentEnabled { get; set; } = true;
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
    public string LocalApiBaseUrl { get; set; } = "http://vendomat.local:1326";
    public string PublicApiBaseUrl { get; set; } = string.Empty;
    public string CloudApiBaseUrl { get; set; } = "https://signal.dllsoft.ro/erp";
    public string CloudMachineToken { get; set; } = string.Empty;
    public string CompanionAccessToken { get; set; } = string.Empty;
    public string AdminPasscodeHash { get; set; } = AdminPasscodeHasher.DefaultHash;
    public TimeSpan PromoRotationInterval { get; set; } = TimeSpan.FromSeconds(8);
    public List<CashChannelSetting> CashChannels { get; set; } = CreateDefaultCashChannels();

    public decimal StockFillPercent =>
        TankCapacityLiters <= 0
            ? 0
            : Math.Clamp(CurrentStockLiters / TankCapacityLiters, 0m, 1m);

    private static List<CashChannelSetting> CreateDefaultCashChannels() =>
    [
        new CashChannelSetting { Channel = 1, Label = "1 RON", Amount = 1m, IsEnabled = true },
        new CashChannelSetting { Channel = 2, Label = "5 RON", Amount = 5m, IsEnabled = true },
        new CashChannelSetting { Channel = 3, Label = "10 RON", Amount = 10m, IsEnabled = true },
        new CashChannelSetting { Channel = 4, Label = "20 RON", Amount = 20m, IsEnabled = false },
        new CashChannelSetting { Channel = 5, Label = "50 RON", Amount = 50m, IsEnabled = false },
        new CashChannelSetting { Channel = 6, Label = "100 RON", Amount = 100m, IsEnabled = false },
    ];
}
