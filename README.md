# OpenPrintDeploy

An open-source alternative to PaperCut Print Deploy. Centralised printer
deployment for Windows fleets, driven by LDAP/AD group membership and zones.

> **Status:** early development. Not yet usable.

## What

If you run a Windows print server with AD and an Intune-managed fleet, you
already have everything you need to deliver shared printers to users — except
the brain that decides *which* printers go to *which* users. OpenPrintDeploy
is that brain.

- **Server** — a .NET service that runs on your print server. REST API +
  Blazor admin UI. Talks LDAP, knows your printers, evaluates "zones".
- **Client** — a small .NET tray app + service deployed via Intune as MSI.
  Calls home at logon and on a timer, installs/removes shared printers to
  match the user's resolved zone set.
- **Zones** — admin-defined bundles of printers, gated on AD group, subnet,
  or computer OU. Any combination, any number of rules.

Windows handles driver delivery itself (point-and-print). OpenPrintDeploy
just tells clients which `\\server\share` UNCs to connect.

## Status

Phase 0 — scaffolding. See `docs/architecture.md` for the public summary,
or the project planning vault for the live design.

## License

MIT (planned — to be added).
