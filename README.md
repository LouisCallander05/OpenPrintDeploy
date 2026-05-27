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

## Repo layout

```
src/
  OpenPrintDeploy.Shared          # DTOs / contracts shared by server + client
  OpenPrintDeploy.Server          # ASP.NET Core 8 — REST API + Blazor admin UI
  OpenPrintDeploy.Client.Service  # .NET 8 Worker — runs as SYSTEM on the endpoint
  OpenPrintDeploy.Client.Tray     # WPF tray app — user session, applies printers
docs/
OpenPrintDeploy.sln
```

## Build

Requires the .NET 8 SDK (pinned via `global.json`).

```sh
dotnet restore
dotnet build
```

The tray app (`OpenPrintDeploy.Client.Tray`) targets `net8.0-windows` and
must be built on Windows. The server and client service are cross-platform
at build time and can be built on Linux / macOS for dev.

To run the server locally:

```sh
dotnet run --project src/OpenPrintDeploy.Server
# → http://localhost:5080/health
```

## License

MIT (planned — to be added).
