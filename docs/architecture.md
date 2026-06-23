# Architecture

See the planning vault for the canonical, live version:
`Obsidian/Alfred/projects/OpenPrintDeploy/`

This file is a public-facing summary mirrored for repo readers.

## Components

- **OpenPrintDeploy.Server** — ASP.NET Core 8 Windows Service. REST API +
  Blazor Server admin UI. SQLite via EF Core.
- **OpenPrintDeploy.Client.Tray** — WPF tray app running in each user's
  session. Authenticates to the server as the signed-in user (Kerberos via
  `UseDefaultCredentials`), fetches the resolved printer set from `/sync`,
  installs/removes per-user printer connections (`AddPrinterConnection` /
  `DeletePrinterConnection`), and raises balloon toasts.
- **OpenPrintDeploy.Shared** — DTOs and contracts shared by both sides.

## Why the tray app is sufficient

The earlier design also called for a SYSTEM-context Worker service driving
syncs via named-pipe IPC. We dropped it: `/sync` needs the *user's* group
memberships to evaluate zones, and the user-session tray already
authenticates as the user via Kerberos. A SYSTEM service would authenticate
as the computer account — wrong identity for zone resolution. The tray runs
per-user, owns the HTTP client, and does its own `Add-Printer` /
`Remove-Printer` — no IPC needed.

## Auth

- Admin browser → server: Negotiate (Windows Integrated Auth)
- Client → server: Negotiate / Kerberos
- Server → LDAP: Negotiate (Kerberos) using the server process identity. On a
  domain-joined print server running as Local SYSTEM that's the computer
  account; nothing about the bind lives in config. A `Basic` bind with a
  configured `BindDn`/`BindPassword` is supported for non-joined hosts.
  **LDAPS by default** (`Directory:Ldap:UseSsl=true`, port 636) so credential
  binds and lookups aren't cleartext — requires a DC LDAPS cert the server
  trusts (standard with AD CS).
- **Dev header auth** (`X-Dev-User`) is registered *only* in the Development
  environment (`AuthExtensions`); production uses Basic/Negotiate. As a backstop
  the server refuses to start if it's running as a Windows service while
  `ASPNETCORE_ENVIRONMENT=Development` — the one way the header-spoof scheme
  could otherwise reach a production host.

### Admin access & first-run

Admin grants are an editable group/user list (`admin-access.json`, under
ProgramData) unioned with break-glass grants in `appsettings.json`
(`Auth:Admin`). When *nothing* is configured anywhere the system is "open" —
any authenticated user is an admin — so the first person can bootstrap.

To keep that open window tiny, the server MSI opens the admin UI on install and
the app **forces first-run setup**: while no admin is configured, every admin
page redirects to `/setup`, which makes the operator name an admin AD group
(validated against the directory) before anything else. Saving a group closes
the open state; `/setup` then redirects away and can't re-open it. A "make my
account an admin too" failsafe and the appsettings break-glass guard against
lock-out. Pre-seeding `Auth:Admin:Groups`/`GroupSids` in appsettings skips
onboarding entirely (the system is never open).

A **corrupt/unreadable** `admin-access.json` fails **closed**, not open: it's
treated as "sealed" (appsettings break-glass grants only), never collapsing back
to the open "any authenticated user" state. A damaged file can lock admins out
(recover via appsettings) but can never silently re-grant the whole domain.

### Client print-server allow-list

The tray validates every server-supplied UNC before handing it to the spooler:
it must be a well-formed `\\host\share`, and its host must be on the allow-list
(`PrinterUncPolicy`). The list defaults to the configured server's host, widened
via `ALLOWEDSERVERS=` / registry / appsettings for split print servers. Defence
in depth on top of TLS pinning — a compromised server still can't point the
spooler at an arbitrary host (NTLM-relay / malicious point-and-print).

## Transport security (TLS)

Windows Integrated Auth (Negotiate/NTLM) over plain HTTP is sniffable and
relay-able on a shared LAN, so the server is **HTTPS-only by default**. Out of
the box (`appsettings.json` → `Https`): `Enabled=true`, `RequireHttps=true`,
`HttpPort=0`, `HttpsPort=5443`. The server MSI opens the firewall on **5443**
only and points the admin shortcut at `https://localhost:5443/`. (Development —
`appsettings.Development.json` and the in-process tests — stays HTTP-only.)

On first boot the server provisions a certificate: an operator-supplied
`Https:PfxPath`, else a self-signed cert persisted to the machine store (its
thumbprint shows on the admin **Settings → Connection security** card). A cert
that fails to provision degrades to an emergency HTTP listener on 5080
(reachable on loopback only, since the firewall opens 5443) rather than failing
startup.

Clients must trust the server cert. Three options:

- **Operator/CA cert** (`Https:PfxPath`) — clients already trust the issuer;
  nothing to distribute.
- **Push the self-signed public cert** to clients' Trusted Root (GPO/Intune).
- **Pin the thumbprint** on the client — `CERTTHUMBPRINT=` on the client MSI
  (`msiexec /i "OpenPrintDeploy.Client.msi" SERVER="https://host:5443"
  CERTTHUMBPRINT="AB12…"`), or `Server:CertificateThumbprint` /
  `OPD_SERVER_CERT_THUMBPRINT` / registry `ServerCertificateThumbprint`. The tray
  then trusts exactly that one cert without a trust-store push. The self-signed
  cert is long-lived (`Https:SelfSignedValidityYears`, default 100, clamped
  1..100) so its thumbprint stays stable for the deployment's life — a pin
  doesn't break on rotation because in practice it never rotates. The expiry
  date shows on the admin Settings card.

Client server URL: an explicit `SERVER=` (or appsettings `Server:BaseAddress` /
`OPD_SERVER_URL`) is honoured as-is; the MSI-filename fallback
(`OpenPrintDeploy - host.msi`) derives `https://<host>:5443`.

### Rotating / renewing the self-signed cert (runbook)

A certificate's validity can't be changed in place, so "renewing" or lengthening
the self-signed cert means **generating a new one** — which gets a **new
thumbprint** and therefore requires **re-pinning every client**. Because the
provisioner *reuses* an existing valid cert (it only regenerates within 7 days of
expiry), a cert minted by an older, shorter-lived build keeps its old expiry
until you force a regen. To do it deliberately:

1. Ensure the server runs a build with the validity you want
   (`Https:SelfSignedValidityYears`, default 100).
2. Admin **Settings → Connection security → Regenerate certificate** (or delete
   the cert with friendly name *"OpenPrintDeploy Server (self-signed)"* from
   `certlm.msc` → Personal). The running server keeps serving until restart.
3. `Restart-Service OpenPrintDeployServer` — a fresh cert is minted on startup.
4. Read the new thumbprint off the Settings card and re-pin clients
   (`CERTTHUMBPRINT=` / registry `ServerCertificateThumbprint`).

Avoid the whole cycle with an operator/CA cert (`Https:PfxPath`): trust is by
issuer, so renewals don't change what clients trust — no pinning, no re-pin.

### Migrating an existing HTTP fleet

HTTPS-only means a client still pointed at `http://host:5080` breaks. To move a
live fleet gradually instead, set `Https:HttpPort=5080` and
`Https:RequireHttps=false` so HTTP and HTTPS coexist, reissue clients onto HTTPS
(with trust distributed), then return to the HTTPS-only defaults and re-open the
firewall on 5443 / close 5080.

## Data model

Printer, Zone, ZoneRule, ZonePrinter, Client, AuditLog. See planning vault
`01 - Architecture.md` for fields.

### Persistence

SQLite via EF Core. Every connection opens with `journal_mode=WAL`,
`synchronous=NORMAL`, and `busy_timeout=5000` (`SqlitePragmaInterceptor`) so a
logon-storm of clients serialises on contended writes instead of failing with
`SQLITE_BUSY` and dropping audit rows. Migrations run on startup; before applying
any pending migration the server snapshots the DB file (+ `-wal`/`-shm`) to a
timestamped `.bak` so a bad migration on the live fleet DB is recoverable.

This is sized for a school-scale fleet. For larger or multi-server deployments,
move `ConnectionStrings:AppDb` to **SQL Server** (the EF model is provider-
agnostic) — that's the scale path beyond SQLite+WAL, and it removes the
single-writer ceiling entirely.

## Zone evaluation

For a `(user)` request:

1. Resolve user's transitive AD group memberships (`tokenGroups`).
2. Find zones with at least one ZoneRule whose group SID the user holds.
3. Union the assigned printers across matched zones.
4. Return `{ printers: [...] }`.

Zone `Priority` is a sort order in the admin UI only — it does not affect
evaluation.

## Sync triggers

- User logon
- Every 60 minutes
- Manual "Sync now" from tray

## Out of scope for v1

- SYSTEM-context Worker service / named-pipe IPC — **rejected, not pending** (see
  "Why the tray app is sufficient"). A SYSTEM service authenticates as the
  computer account, which carries the wrong group memberships for user-zone
  resolution. The per-user tray is the correct identity.
- macOS / Linux client
- Driver packaging (trust point-and-print)
- Cost tracking, quotas, pull printing
- Cloud-hosted server
