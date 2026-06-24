namespace Vendomat.Controller.Domain.Sales;

/// <summary>
/// Single source of truth for the user-facing payment/dispense error copy raised by the
/// runtime. Centralizing these removes duplicated literals and provides the seam for
/// localizing them at the UI boundary (the domain layer must not depend on a UI
/// localization service, so the strings live here as keys/defaults).
/// </summary>
public static class DispenseMessages
{
    public const string PaymentMethodLockedDuringOperation = "Metoda de plata nu poate fi schimbata in timpul unei operatii.";
    public const string PaymentMethodUnsupported = "Metoda de plata nu este suportata.";
    public const string CashDisabled = "Plata cu numerar este dezactivata.";
    public const string CardDisabled = "Plata cu cardul este dezactivata.";
    public const string CashInProgressFinishFirst = "A fost introdus numerar. Finalizeaza sesiunea cash inainte de plata cu cardul.";
    public const string CardUnavailableCashSession = "A fost introdus numerar. Plata cu cardul nu mai este disponibila pentru sesiunea curenta.";
    public const string CreditCannotBeNegative = "Valoarea creditului nu poate fi negativa.";
    public const string SelectQuantityFirst = "Selecteaza cantitatea inainte de start.";
    public const string InsufficientCredit = "Creditul introdus este insuficient pentru cantitatea selectata.";
    public const string MachineBusy = "Masina executa deja o operatie.";
    public const string DispenseRequiresEsp32 = "Dozarea in Production necesita ESP32 activ.";
    public const string Esp32CommandFailed = "Comanda ESP32 nu a putut fi trimisa.";
    public const string PrimingVolumeRange = "Amorsarea permite volume intre 50 ml si 1000 ml.";
    public const string PrimingRequiresEsp32 = "Amorsarea in Production necesita ESP32 activ.";
    public const string PrimingCommandFailed = "Comanda de amorsare nu a putut fi trimisa catre ESP32.";
    public const string SanitationBlockedDuringOperation = "Curatarea nu poate porni in timpul altei operatii.";
    public const string FirmwareRequestMissing = "Cererea OTA pentru ESP32 lipseste.";
    public const string FirmwareUrlRequired = "URL-ul firmware-ului ESP32 este obligatoriu.";
    public const string Esp32Disabled = "ESP32 este dezactivat din setari.";
}
