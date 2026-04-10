# Reuse Audit From ProjectVending

## De refolosit direct

- `ProjectVending/Vendomat.Common/SSP`
  - stack-ul SSP și modelele validatorului sunt mutate în `Vendomat.Controller.Hardware/Legacy/SSP`;
- `ProjectVending/Vendomat.Common/BillValidator/Nv9Usb.cs`
  - wrapper-ul NV9USB este reutilizat în `Nv9BillValidatorGateway`;
- `ProjectVending/ControlModule/ControlModule.ino`
  - util ca punct de pornire pentru protocolul serial JSON cu ESP32;
- `ProjectVending/YfApi`
  - merită păstrat pentru device management pe tabletele YoungFeel, dar trebuie adus curat în soluția nouă.

## De refolosit doar ca idee, nu ca implementare

- `Vendomat.Main/AppSettings.cs`
  - conceptul de setări persistente este bun, dar implementarea statică trebuie evitată;
- `Vendomat.Main/Services/ArduinoService.cs`
  - protocolul JSON este util, dar clasa actuală este prea legată direct de UI și porturi hardcodate;
- `Vendomat.Main/Views/SetingsPage.xaml`
  - secțiunile de configurare sunt utile, însă layout-ul trebuie simplificat și decuplat de logică;
- `Vendomat.Mobile`
  - modelele și ideea de pairing rămân utile, dar aplicația mobilă trebuie refăcută peste API-ul local nou.

## De evitat în forma actuală

- `Vendomat.Main/ViewModels/MainPageViewModel.cs`
  - combină UI, validator, ESP32 și logică de business într-un singur viewmodel;
- `Vendomat.Main/Services/DatabaseService.cs`
  - prea generic, fără modele dedicate și fără claritate pe entitățile persistate;
- `Vendomat.Main/ApiControllers/SettingsController.cs`
  - serverul local există, dar prea minimal pentru pairing și management real;
- `Vendomat.Main/Settings` static singleton
  - face testarea și evoluția aplicației mai grea.

## Observații importante

- pe conexiune GSM, nu e sigur să ne bazăm pe acces inbound direct către IP-ul tabletei;
- de aceea, `fiecare vendomat este propriul server` rămâne valabil local, dar pentru remote admin trebuie sincronizare outbound sau relay;
- în soluția nouă, pairing-ul QR trebuie să transporte `machineId + pairingCode + local api hint`, nu doar IP.
