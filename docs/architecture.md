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

## Data model

Printer, Zone, ZoneRule, ZonePrinter, Client, AuditLog. See planning vault
`01 - Architecture.md` for fields.

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

- macOS / Linux client
- Driver packaging (trust point-and-print)
- Cost tracking, quotas, pull printing
- Cloud-hosted server
