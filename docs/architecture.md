# Architecture

See the planning vault for the canonical, live version:
`Obsidian/Alfred/projects/OpenPrintDeploy/`

This file is a public-facing summary mirrored for repo readers.

## Components

- **OpenPrintDeploy.Server** — ASP.NET Core 8 Windows Service. REST API +
  Blazor Server admin UI. SQLite via EF Core.
- **OpenPrintDeploy.Client.Service** — .NET 8 Windows Service running as
  SYSTEM. Schedules syncs, owns the HTTP client.
- **OpenPrintDeploy.Client.Tray** — WPF tray app running in the user session.
  Receives instructions from the service via named pipe, performs
  `Add-Printer`/`Remove-Printer`, raises toasts.
- **OpenPrintDeploy.Shared** — DTOs and contracts shared by all projects.

## Why two client processes

`Add-Printer -ConnectionName` writes to HKCU — the per-user registry hive. A
SYSTEM-context service cannot install per-user printer connections. The
service handles networking and policy; the tray app applies the result in
user context.

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
