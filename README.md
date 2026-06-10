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
  OpenPrintDeploy.Client.Core     # Cross-platform sync/reconcile logic
  OpenPrintDeploy.Client.Tray     # WPF tray app — user session, applies printers
installer/
  OpenPrintDeploy.Server.Msi      # WiX per-machine MSI for the server
  OpenPrintDeploy.Client.Installer # Per-machine installer for the tray (Intune-deployable)
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
# One-time: enable dev auth + the in-memory Stub directory (no AD required).
# appsettings.Development.json is gitignored; copy the committed template.
cp src/OpenPrintDeploy.Server/appsettings.Development.json{.example,}

dotnet run --project src/OpenPrintDeploy.Server
# → http://localhost:5080/health
```

Without `appsettings.Development.json` the server falls back to the production
defaults (Negotiate auth + LDAP), so `/sync` will 401/500 on a box with no AD.

## Production: domain-joined print server

### Deploy

From this repo on a dev machine with the .NET 8 SDK:

```powershell
.\scripts\Publish-Server.ps1
```

This produces a single per-machine MSI at `publish/OpenPrintDeploy.Server.msi`,
built with the [WiX Toolset][wix] from a self-contained `win-x64` publish (the
WiX SDK and extensions restore from NuGet automatically — no global tool to
install). The target machine doesn't need the .NET 8 hosting bundle.

Copy `OpenPrintDeploy.Server.msi` to the print server, then install it — either:

- **Double-click it** (UAC elevates automatically), or
- Silently / scripted: `msiexec /i OpenPrintDeploy.Server.msi /quiet`

`msiexec` isn't subject to PowerShell script-block policies, so EDR products
that block PowerShell by default (Cylance, etc.) won't reject it. The MSI
installs to `C:\Program Files\OpenPrintDeploy`, registers `OpenPrintDeployServer`
as a Windows service (Local SYSTEM, autostart, restart-on-failure), opens TCP
5080 in the firewall, adds a Start Menu shortcut (**OpenPrintDeploy →
OpenPrintDeploy Admin**), and starts the service. The database lives at
`C:\ProgramData\OpenPrintDeploy\app.db` and survives upgrades and uninstalls.

Open the admin UI from the Start Menu shortcut, or from a workstation:

```
http://<print-server>:5080/admin/directory
```

Click **Test connection**. If LDAP, DC and search base auto-resolve and the
bind succeeds, you're done. Logs go to **Event Viewer → Windows Logs →
Application** (source: `OpenPrintDeployServer`).

To upgrade, just install a newer MSI — it replaces the prior version in place
and keeps the database. To remove, use **Settings → Apps** (or **Add/Remove
Programs**), or:

```cmd
msiexec /x OpenPrintDeploy.Server.msi          :: or "Uninstall" from Apps
```

Uninstall stops and removes the service and firewall rule. The database under
`C:\ProgramData\OpenPrintDeploy` is left in place; delete that folder by hand if
you want a clean wipe.

[wix]: https://wixtoolset.org/

## Endpoints: the tray client

The client ships as a **single self-extracting installer exe** — one file that
carries the tray (and its runtime) inside it. The tray is per-machine installed
and auto-starts in every user's session via an `HKLM\…\Run` key; it
authenticates to the server as the signed-in user via Kerberos, calls `/sync`,
and applies the resolved printers to the user's per-user (HKCU) connection list.

**Download it straight from the server.** On the admin dashboard, click
**Download client installer** (or `GET /download/client`). The server hands back
the installer already named for itself — `OpenPrintDeploy - <host>.exe` — so the
file is pre-configured for that server with nothing to type. (The tag-push
release also publishes the bare installer as `OpenPrintDeploy-client-win-x64.zip`.)

Install on a single workstation — any of:

```cmd
:: 1. Just run the downloaded "OpenPrintDeploy - <host>.exe" (double-click -> UAC).
::    It reads <host> from its own filename and configures http://<host>:5080.

:: 2. Or pass the server explicitly (overrides the filename):
OpenPrintDeploy.Client.Installer.exe install --server http://printsrv01.corp.local:5080
```

**Filename-based config:** a file named `OpenPrintDeploy - <host>.exe` configures
`http://<host>:5080`. Filenames can't carry a scheme or port, so `http` and port
`5080` are applied automatically; pass `--server` to override. This is the path
for non-technical deployers and for Intune — name once, run anywhere.

The installer extracts binaries to `C:\Program Files\OpenPrintDeploy\Tray\`,
writes `appsettings.json` with the server URL, and registers the Run-key
auto-start. The tray will launch at next user logon; right-click its system
tray icon to see "Sync now", "Sign in…", the configured server, and the version.

### Non-domain-joined machines (sign in with a domain account)

Integrated auth (Kerberos/NTLM as the signed-in user) only works when the
machine is joined to the AD domain or to Entra. On a standalone (workgroup)
laptop the logged-in user has no domain identity, so `/sync` would be rejected.

The tray detects this (via `dsregcmd /status`): when the device is **not**
domain- or Entra-joined, it prompts for a domain account (`DOMAIN\user` or
`user@domain`) and authenticates with those credentials instead — SSPI uses NTLM
with the supplied account, and the domain-joined server validates it against a
domain controller. The same prompt appears as a fallback if integrated auth is
ever rejected with a 401, and on demand via the **"Sign in…"** tray item.

Credentials are saved in the **Windows Credential Manager** (per user, under
target `OpenPrintDeploy:<server-host>`), so the prompt appears only once until
the password changes. No server configuration is required — the server's
existing Negotiate authentication already accepts these credentials.

To uninstall:

```cmd
OpenPrintDeploy.Client.Installer.exe uninstall                :: leaves per-user state
OpenPrintDeploy.Client.Installer.exe uninstall --remove-data  :: wipes installer's user state too
```

### Intune deployment

For fleet rollout, download the pre-named installer from the server (or grab the
release exe), drop that one file in a folder, and wrap it with Microsoft's
[IntuneWinAppUtil][intunewin] as a Win32 app:

```cmd
IntuneWinAppUtil.exe -c <folder-with-the-exe> -s "OpenPrintDeploy - printsrv01.corp.local.exe" -o <out>
```

Intune install command — if you used the server's pre-named download, the
filename already carries the server, so just run it:

```
"OpenPrintDeploy - printsrv01.corp.local.exe" install
```

Or, with the bare installer, pass the server explicitly:

```
OpenPrintDeploy.Client.Installer.exe install --server http://printsrv01.corp.local:5080
```

Intune uninstall command:

```
OpenPrintDeploy.Client.Installer.exe uninstall
```

Suggested detection rule (Registry):

- Key: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`
- Value: `OpenPrintDeployTray`
- Detection: value exists.

[intunewin]: https://github.com/Microsoft/Microsoft-Win32-Content-Prep-Tool

### Identity & directory config

- The service runs as **Local SYSTEM** by default — it authenticates to AD as
  the computer account (`PRINTSRV01$`). No LDAP bind password lives in config.
  To run as a domain service account instead, set the service's logon identity
  in services.msc after install.
- Leave `Directory:Ldap:Server` and `Directory:Ldap:SearchBase` blank — the
  server discovers a DC and the domain DN via `Domain.GetCurrentDomain()` at
  first use. Override them if you need to pin a specific DC.
- Set `Directory:Ldap:AuthMode` to `Basic` (and supply `BindDn`/`BindPassword`)
  only when the host isn't domain-joined.
- **Multiple domains / trusts.** Zone matching uses the group SIDs in the user's
  authenticated Kerberos/NTLM token, which already span every trusted domain. So
  a server joined to one domain (e.g. students) correctly resolves users from a
  trusted domain (e.g. staff) — no multi-domain LDAP config needed. The LDAP
  settings only drive the admin group *picker* and SID→name labels, which see the
  server's own domain; for a group in another domain, paste its SID into the zone
  rule (find it via **Directory → Check my access** signed in as that user). The
  directory lookup is only a fallback for identities whose token carries no
  groups (e.g. the dev header auth).

## License

MIT (planned — to be added).
