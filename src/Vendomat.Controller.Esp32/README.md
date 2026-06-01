# Vendomat.Controller.Esp32

Firmware ESP32 separat de proiectele .NET.

## Ce face

- trimite raspunsuri si telemetrie pe `USB Serial` si pe `UART2`
- primeste comenzi de la tableta pe `UART2` la `115200`
- foloseste `RXD2 = GPIO16` si `TXD2 = GPIO17`
- trimite snapshot senzor `DHT22` in format JSON: `Type = 0`
- primeste comenzi compatibile cu `Esp32SerialGateway`:
  - `Type = 2` dozare
  - `Type = 3` curatare continua
  - `Type = 4` stop
  - `Type = 5` curatare pulsata
  - `Type = 20` update firmware OTA
  - `Type = 100` probe / acknowledge
- raporteaza progres dozare `Type = 1`
- confirma finalul dozarii cu `Type = 11`

## Structura

- `platformio.ini` config build pentru ESP32
- `include/FirmwareConfig.h` pini si constante hardware
- `src/main.cpp` logica firmware

## Setup rapid in Visual Studio Code

1. Deschide workspace-ul in `VS Code`.
2. Instaleaza extensiile recomandate:
   - `PlatformIO IDE`
   - `C/C++`
3. In bara `PlatformIO`, deschide proiectul din `src/Vendomat.Controller.Esp32`.
4. Editeaza pinii din `include/FirmwareConfig.h` daca placa ta foloseste alte conexiuni.
5. Ruleaza:
   - `PlatformIO: Build`
   - `PlatformIO: Upload`
   - `PlatformIO: Serial Monitor`

## Setup minim Visual Studio 2022

Visual Studio clasic nu are suport ESP32 la fel de bun ca VS Code + PlatformIO.

Varianta practica:

1. instaleaza extensia `Visual Micro`
2. deschide folderul `src/Vendomat.Controller.Esp32`
3. selecteaza placa `ESP32 Dev Module`
4. foloseste acelasi cod si aceiasi pini din `FirmwareConfig.h`

## Comenzi asteptate

### Probe

```json
{"Type":100,"Probe":true}
```

### Dozare

```json
{"Type":2,"TargetLiters":0.5,"PulsesPerLiter":450}
```

### Curatare continua

```json
{"Type":3,"DurationSeconds":20}
```

### Curatare pulsata

```json
{"Type":5,"DurationSeconds":20,"PulseOnMilliseconds":500,"PulseOffMilliseconds":500}
```

### Update OTA

```json
{"Type":20,"Url":"http://192.168.43.1:1326/firmware/esp32.bin","WifiSsid":"VendomatHotspot","WifiPassword":"parola123","ExpectedMd5":""}
```

## Flux recomandat pentru update din tableta Android

1. tableta expune un URL HTTP cu fisierul `.bin`
2. ESP32 primeste comanda `Type = 20` pe serial
3. ESP32 intra pe Wi-Fi-ul dat in comanda
4. ESP32 descarca firmware-ul si se restarteaza

Update-ul din tableta pe simplu `UART` nu este suficient pentru flash clasic.
Pentru flash serial complet ar trebui si control pe liniile de boot `GPIO0` si `EN`.

## Observatii hardware

- `PumpRelayPin` este setat implicit pe `25`
- `FlowPulsePin` este setat implicit pe `23`
- `DhtPin` este setat implicit pe `27`
- `RXD2` este `16` pentru receptie de la tableta
- `TXD2` este `17` pentru trimitere catre tableta
- daca releul tau este active-low, schimba `PumpRelayActiveHigh` in `false`
