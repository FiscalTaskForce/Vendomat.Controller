# Cloud Broker

## Why this exists

- The phone app cannot reliably reach a dispenser directly once the machine is behind 4G NAT / CGNAT.
- The emulator test already confirmed that direct addressing is fragile even inside the current lab setup.
- The broker keeps costs low because the dispenser only sends small outbound HTTPS sync calls.

## Proposed runtime flow

1. Tablet generates a QR payload with:
- `MachineId`
- short-lived `PairingCode`
- optional local/public endpoints
- `CloudApiBaseUrl`

2. Tablet mirrors that pairing session into the broker.

3. Companion scans the QR and claims pairing against the broker.

4. Broker returns a machine-scoped `CompanionAccessToken`.

5. Companion uses the same familiar routes:
- `GET /api/device/status`
- `GET /api/device/settings`
- `PUT /api/device/settings`
- `POST /api/device/sanitation`

6. Tablet pushes cached status snapshots to the broker and receives pending commands back.

## Isolation model

- Every dispenser has its own `MachineId`.
- Every dispenser has its own `CloudMachineToken`.
- Every companion pairing results in a machine-scoped `CompanionAccessToken`.
- A token can only read or write the dispenser it was paired with.

This keeps users separated without needing a heavy multi-tenant identity platform on day one.

## Traffic model

- Idle sync interval: `60s`
- Busy sync interval: `10s` while dispensing or cleaning
- Commands are queued server-side and delivered on sync

This is intentionally cheaper than permanent high-frequency polling and simpler than requiring inbound connectivity to the tablet.

## Deployment note for signal.dllsoft.ro

- The new project is `src/Vendomat.Controller.Cloud`
- It can be published to `\\192.168.100.2\wwwroot\erp`
- If the IIS app is served under `/erp`, set `Cloud:PathBase=/erp`

Recommended public URL:

- `https://signal.dllsoft.ro/erp`

Recommended tablet setting:

- `CloudApiBaseUrl = https://signal.dllsoft.ro/erp`

## Current limitation

- The broker foundation is implemented and builds successfully.
- A real end-to-end internet test still requires publishing the cloud app and pointing at least one tablet to the public broker URL.
