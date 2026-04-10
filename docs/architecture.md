# Vendomat Controller Architecture

## Alegere UI

Pentru tableta controllerului recomandarea curentă este `MAUI simplu (XAML + MVVM)`, nu `MAUI Hybrid Blazor`.

Motive:

- integrarea cu Android, serial, fullscreen și kiosk mode este mai directă;
- pornirea este mai simplă și mai predictibilă pe device-uri dedicate;
- flow-ul offline-first și polling-ul local se potrivesc bine cu viewmodel-uri native;
- putem adăuga ulterior Rive sau Lottie fără să schimbăm arhitectura.

## Proiecte

- `src/Vendomat.Controller.Domain`
  - modele pure pentru setări, tranzacții, pairing, igienizare și stare runtime;
- `src/Vendomat.Controller.Application`
  - contracte și interfețe pentru runtime, pairing, stocare locală și API local;
- `src/Vendomat.Controller.Hardware`
  - protocol ESP32 și codul SSP/NV9 reutilizat din proiectul vechi;
- `src/Vendomat.Controller.Tablet`
  - aplicația MAUI Android, baza locală SQLite, UI-ul și serverul local EmbedIO.

## Principii

- `offline first`: vânzările, log-urile, setările și igienizările rămân local pe tabletă;
- `thin sync`: pe GSM se trimit doar rezumate și batch-uri, nu polling continuu;
- `local server`: tableta expune endpoint-uri locale pentru status și pairing;
- `hardware isolation`: UI-ul nu mai vorbește direct cu serial sau SSP;
- `one source of truth`: `MachineRuntimeService` compune starea mașinii.

## Flux local recomandat

1. Tableta pornește.
2. Inițializează SQLite local.
3. Pornește API-ul local pe `:1326`.
4. Încarcă setările și reclamele locale.
5. UI-ul citește snapshot-uri din runtime service.
6. Hardware-ul real va injecta creditul, temperatura și progresul de dozare.

## Ce urmează

- integrarea reală ESP32 prin serial în locul simulării curente;
- legarea validatorului NV9/SSP în runtime service;
- portarea `YfApi` în noua soluție pentru lock-down complet al interfeței;
- ecran dedicat pentru istoric vânzări, log-uri și rapoarte;
- sincronizare batch cu backend central, nu conexiuni inbound directe prin GSM.
