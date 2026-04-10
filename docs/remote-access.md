# Remote Access And Pairing

## Current foundation

- Every dispenser has a stable `MachineId`.
- The QR payload contains:
  - `MachineId`
  - short-lived `PairingCode`
  - `LocalApiBaseUrl`
  - `PublicApiBaseUrl`
- The companion app claims pairing through `POST /api/pairing/claim`.
- After a valid claim, the dispenser returns a long-lived companion token.
- Companion requests send `X-Vendomat-Token`.
- Protected endpoints:
  - `GET /api/device/status`
  - `GET /api/device/settings`
  - `PUT /api/device/settings`
  - `POST /api/device/sanitation`
  - `GET /api/pairing/current`

## Why public endpoint is needed

On 4G routers, the dispenser will often sit behind carrier NAT / CGNAT. That means inbound connections from the phone to the tablet usually cannot reach the device directly, even if the tablet has mobile internet.

Because of that, the public endpoint should be provided by an outbound tunnel or relay, not by raw port forwarding.

## Recommended rollout

1. Local testing

- Set `LocalApiBaseUrl` to the LAN address of the tablet or router.
- Leave `PublicApiBaseUrl` empty.
- Pair and verify the phone app on the same Wi-Fi / local network.

2. Internet testing

- Publish the local API through a tunnel service.
- Put the generated HTTPS URL into `PublicApiBaseUrl`.
- Generate a new QR code.
- Pair again from the phone.
- Verify that the companion app works with Wi-Fi disabled on the phone.

## Good tunnel options

- Cloudflare Tunnel
- Tailscale Funnel

Both work well with outbound-only connectivity, which is a better fit for 4G deployments than direct inbound access.

## Operational notes

- If `PublicApiBaseUrl` changes, generate a new QR code and re-pair the companion app.
- If we later want stronger access control, we can rotate the companion token from the admin settings page.
- If we want central fleet management later, `MachineId` is already in place and can be used as the cloud identity for each dispenser.
